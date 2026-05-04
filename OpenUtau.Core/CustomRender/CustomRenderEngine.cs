﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Core.CustomRender {
    public class CustomRenderEngine {
        public static bool ShouldUseCustomRenderEngine(UProject project) {
            if (project == null || project.parts == null) {
                return false;
            }
            
            return project.parts
                .Where(part => part is UVoicePart)
                .Cast<UVoicePart>()
                .Any(part => {
                    var request = part.GetRenderRequest();
                    return request != null && request.phrases.Any(p => p.renderer is CustomServerRenderer);
                });
        }
        
        public static object CreateRenderEngine(UProject project, int startTick = 0, int endTick = -1, int trackNo = -1) {
            if (ShouldUseCustomRenderEngine(project)) {
                return new CustomRenderEngine(project, startTick, endTick, trackNo);
            } else {
                return new Render.RenderEngine(project, startTick, endTick, trackNo);
            }
        }
        readonly UProject project;
        readonly int startTick;
        readonly int endTick;
        readonly int trackNo;
        readonly int maxConcurrency;
        readonly string serverUrl;

        public CustomRenderEngine(
            UProject project, 
            int startTick = 0, 
            int endTick = -1, 
            int trackNo = -1,
            int maxConcurrency = 2,
            string serverUrl = "http://localhost:8000/synthesize") {
            this.project = project;
            this.startTick = startTick;
            this.endTick = endTick;
            this.trackNo = trackNo;
            this.maxConcurrency = maxConcurrency;
            this.serverUrl = serverUrl;
        }

        internal Tuple<WaveMix, List<Fader>> RenderMixdown(TaskScheduler uiScheduler, ref CancellationTokenSource cancellation, bool wait = false) {
            var newCancellation = new CancellationTokenSource();
            var oldCancellation = Interlocked.Exchange(ref cancellation, newCancellation);
            if (oldCancellation != null) {
                oldCancellation.Cancel();
                oldCancellation.Dispose();
            }
            double startMs = project.timeAxis.TickPosToMsPos(startTick);
            double endMs = endTick == -1 ? double.PositiveInfinity : project.timeAxis.TickPosToMsPos(endTick);
            var faders = new List<Fader>();
            var requests = PrepareRequests()
                .Where(request => request.sources.Length > 0 && request.sources.Max(s => s.EndMs) > startMs && (double.IsPositiveInfinity(endMs) || request.sources.Min(s => s.offsetMs) < endMs))
                .ToArray();
            for (int i = 0; i < project.tracks.Count; ++i) {
                if (trackNo != -1 && trackNo != i) {
                    continue;
                }
                var track = project.tracks[i];
                var trackRequests = requests
                    .Where(req => req.trackNo == i)
                    .ToArray();
                var trackSources = trackRequests.Select(req => req.mix)
                    .OfType<ISignalSource>()
                    .ToList();
                trackSources.AddRange(project.parts
                    .Where(part => part is UWavePart && part.trackNo == i)
                    .Select(part => part as UWavePart)
                    .Where(part => part.Samples != null)
                    .Select(part => part.TrimSamples(project)));
                var trackMix = new WaveMix(trackSources);
                var fader = new Fader(trackMix);
                fader.Scale = PlaybackManager.DecibelToVolume(track.Muted ? -24 : track.Volume);
                fader.Pan = (float)track.Pan;
                fader.SetScaleToTarget();
                faders.Add(fader);
            }
            var task = Task.Run(async () => {
                await RenderRequests(requests, newCancellation, playing: !wait).ConfigureAwait(false);
            });
            task.ContinueWith(task => {
                if (task.IsFaulted && !wait) {
                    Log.Error(task.Exception.Flatten(), "Failed to render.");
                    PlaybackManager.Inst.StopPlayback();
                    MessageCustomizableException customEx;
                    if (task.Exception.Flatten().InnerExceptions.ToList().Any(e => e is DllNotFoundException)) {
                        customEx = new MessageCustomizableException("Failed to render.", "<translate:errors.failed.render>: <translate:errors.install.cpp>", task.Exception);
                    } else {
                        customEx = new MessageCustomizableException("Failed to render.", "<translate:errors.failed.render>", task.Exception);
                    }
                    DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(customEx));
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, uiScheduler);
            if (wait) {
                task.Wait();
            }
            return Tuple.Create(new WaveMix(faders), faders);
        }

        internal Tuple<MasterAdapter, List<Fader>> RenderProject(TaskScheduler uiScheduler, ref CancellationTokenSource cancellation) {
            double startMs = project.timeAxis.TickPosToMsPos(startTick);
            var renderMixdownResult = RenderMixdown(uiScheduler, ref cancellation, wait: false);
            var master = new MasterAdapter(renderMixdownResult.Item1);
            master.SetPosition((int)(startMs * 44100 / 1000) * 2);
            return Tuple.Create(master, renderMixdownResult.Item2);
        }

        internal List<WaveMix> RenderTracks(TaskScheduler uiScheduler, ref CancellationTokenSource cancellation) {
            var newCancellation = new CancellationTokenSource();
            var oldCancellation = Interlocked.Exchange(ref cancellation, newCancellation);
            if (oldCancellation != null) {
                oldCancellation.Cancel();
                oldCancellation.Dispose();
            }
            var trackMixes = new List<WaveMix>();
            var requests = PrepareRequests();
            if (requests.Length == 0) {
                return trackMixes;
            }
            int maxTrackNo = requests.Max(req => req.trackNo);
            for (int trackNo = 0; trackNo <= maxTrackNo; trackNo++) {
                var trackRequests = requests.Where(req => req.trackNo == trackNo).ToArray();
                if (trackRequests.Length == 0) {
                    trackMixes.Add(null);
                } else {
                    RenderRequests(trackRequests, newCancellation);
                    var mix = new WaveMix(trackRequests.Select(req => req.mix).ToArray());
                    trackMixes.Add(mix);
                }
            }
            return trackMixes;
        }

        internal void PreRenderProject(ref CancellationTokenSource cancellation) {
            var newCancellation = new CancellationTokenSource();
            var oldCancellation = Interlocked.Exchange(ref cancellation, newCancellation);
            if (oldCancellation != null) {
                oldCancellation.Cancel();
                oldCancellation.Dispose();
            }
            _ = Task.Run(async () => {
                try {
                    await Task.Delay(200, newCancellation.Token).ConfigureAwait(false);
                    if (newCancellation.Token.IsCancellationRequested) {
                        return;
                    }
                    RenderRequests(PrepareRequests(), newCancellation);
                } catch (OperationCanceledException) {
                    // Cancellation is expected, ignore
                } catch (Exception e) {
                    if (!newCancellation.IsCancellationRequested) {
                        Log.Error(e, "Failed to pre-render.");
                    }
                }
            });
        }

        private RenderPartRequest[] PrepareRequests() {
            RenderPartRequest[] requests;
            SingerManager.Inst.ReleaseSingersNotInUse(project);
            lock (project) {
                requests = project.parts
                    .Where(part => part is UVoicePart && (trackNo == -1 || part.trackNo == trackNo))
                    .Where(part => !Preferences.Default.SkipRenderingMutedTracks || !project.tracks[part.trackNo].Muted)
                    .Select(part => part as UVoicePart)
                    .Select(part => part.GetRenderRequest())
                    .Where(request => request != null)
                    .Where(request => request.phrases.Any(p => p.renderer is CustomServerRenderer))
                    .ToArray();
            }
            foreach (var request in requests) {
                if (endTick != -1) {
                    request.phrases = request.phrases
                        .Where(phrase => phrase.end > startTick && (endTick == -1 || phrase.position < endTick))
                        .ToArray();
                }
                request.sources = new WaveSource[request.phrases.Length];
                for (var i = 0; i < request.phrases.Length; i++) {
                    var phrase = request.phrases[i];
                    var layout = phrase.renderer.Layout(phrase);
                    double posMs = layout.positionMs - layout.leadingMs;
                    double durMs = layout.estimatedLengthMs;
                    request.sources[i] = new WaveSource(posMs, durMs, 0, 1);
                }
                request.mix = new WaveMix(request.sources);
            }
            return requests;
        }

        private async Task RenderRequests(
            RenderPartRequest[] requests,
            CancellationTokenSource cancellation,
            bool playing = false) {
            if (requests.Length == 0 || cancellation.IsCancellationRequested) {
                return;
            }
            var tuples = requests
                .SelectMany(req => req.phrases
                    .Zip(req.sources, (phrase, source) => Tuple.Create(phrase, source, req)))
                .ToArray();
            if (playing) {
                // 按距离播放起点的距离排序：
                // 1) 播放位置及之后的片段优先（用户最先听到的）
                // 2) 播放位置之前的重叠片段排后面
                // 两组内各自按 position 升序
                Array.Sort(tuples, (a, b) => {
                    bool aAfterStart = a.Item1.position >= startTick;
                    bool bAfterStart = b.Item1.position >= startTick;
                    if (aAfterStart != bAfterStart) {
                        return aAfterStart ? -1 : 1;
                    }
                    return a.Item1.position.CompareTo(b.Item1.position);
                });
            }
            var progress = new Progress(tuples.Sum(t => t.Item1.phones.Length));

            var phrases = tuples.Select(t => t.Item1).ToArray();
            var sources = tuples.Select(t => t.Item2).ToArray();
            var request = tuples.First().Item3;

            var customRenderer = new CustomServerRenderer(serverUrl);

            if (playing) {
                // ===== 播放模式：按位置顺序排队渲染 =====
                // 始终保持最多 maxConcurrency 个 HTTP 请求在执行，
                // 且总是最靠前的未完成片段优先启动，避免后面先渲染完而前面卡顿。
                var inProgress = new List<Task<(int index, RenderResult result)>>();
                int nextToStart = 0;

                while ((inProgress.Count > 0 || nextToStart < phrases.Length)
                       && !cancellation.IsCancellationRequested) {
                    // 补充任务槽位：按顺序启动，保持最多 maxConcurrency 个并发
                    while (inProgress.Count < maxConcurrency && nextToStart < phrases.Length) {
                        int idx = nextToStart++;
                        var phrase = phrases[idx];
                        inProgress.Add(RenderOnePhrase(
                            idx, phrase, progress, request, customRenderer, cancellation));
                    }

                    // 等待任意一个完成（由于按顺序启动，通常靠前的先完成）
                    var completed = await Task.WhenAny(inProgress).ConfigureAwait(false);
                    inProgress.Remove(completed);
                    var (index, result) = await completed.ConfigureAwait(false);
                    sources[index].SetSamples(result.samples);
                }

                if (!cancellation.IsCancellationRequested
                    && request.sources.All(s => s.HasSamples)) {
                    request.part.SetMix(request.mix);
                    DocManager.Inst.ExecuteCmd(new PartRenderedNotification(request.part));
                }
            } else {
                // ===== 非播放模式：全部并发（导出等场景，顺序不重要） =====
                var httpSemaphore = new SemaphoreSlim(maxConcurrency);
                var tasks = new Task<(int index, RenderResult result)>[phrases.Length];

                for (int i = 0; i < phrases.Length; i++) {
                    int idx = i;
                    var phrase = phrases[idx];

                    tasks[idx] = Task.Run(async () => {
                        string? preJson = null;
                        bool needHttp = false;

                        if (phrase.renderer is CustomServerRenderer) {
                            var wavPath = Path.Join(PathManager.Inst.CachePath,
                                $"custom-{phrase.hash:x16}.wav");
                            if (!File.Exists(wavPath)) {
                                preJson = CustomServerRenderer.ConvertPhraseToJson(phrase);
                                needHttp = true;
                            }
                        }

                        if (needHttp) {
                            await httpSemaphore.WaitAsync(cancellation.Token)
                                .ConfigureAwait(false);
                        }
                        try {
                            return await RenderOnePhraseCore(
                                idx, phrase, progress, request, customRenderer,
                                cancellation, preJson).ConfigureAwait(false);
                        } finally {
                            if (needHttp) {
                                httpSemaphore.Release();
                            }
                        }
                    }, cancellation.Token);
                }

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var (index, result) in results) {
                    sources[index].SetSamples(result.samples);
                }
                if (request.sources.All(s => s.HasSamples)) {
                    request.part.SetMix(request.mix);
                    DocManager.Inst.ExecuteCmd(new PartRenderedNotification(request.part));
                }
            }
            progress.Clear();
        }

        /// <summary>
        /// 渲染单个 phrase（播放模式使用）。
        /// 不做并发控制，由调用方（RenderRequests）负责按顺序调度。
        /// </summary>
        private async Task<(int index, RenderResult result)> RenderOnePhrase(
            int idx, RenderPhrase phrase, Progress progress,
            RenderPartRequest request, CustomServerRenderer customRenderer,
            CancellationTokenSource cancellation) {

            string? preJson = null;
            if (phrase.renderer is CustomServerRenderer) {
                var wavPath = Path.Join(PathManager.Inst.CachePath,
                    $"custom-{phrase.hash:x16}.wav");
                if (!File.Exists(wavPath)) {
                    preJson = CustomServerRenderer.ConvertPhraseToJson(phrase);
                }
            }

            return await RenderOnePhraseCore(
                idx, phrase, progress, request, customRenderer,
                cancellation, preJson).ConfigureAwait(false);
        }

        /// <summary>
        /// 渲染单个 phrase 的核心逻辑（播放 / 非播放共用）。
        /// </summary>
        private static async Task<(int index, RenderResult result)> RenderOnePhraseCore(
            int idx, RenderPhrase phrase, Progress progress,
            RenderPartRequest request, CustomServerRenderer customRenderer,
            CancellationTokenSource cancellation, string? preJson) {

            RenderResult result;
            if (phrase.renderer is CustomServerRenderer) {
                result = await customRenderer.RenderImpl(
                    phrase, progress, request.trackNo, cancellation, false, preJson)
                    .ConfigureAwait(false);
            } else {
                result = await phrase.renderer.Render(
                    phrase, progress, request.trackNo, cancellation, false)
                    .ConfigureAwait(false);
            }
            return (idx, result);
        }
    }
}
