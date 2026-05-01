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
            int maxConcurrency = 4,
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
                Array.Sort(tuples, (a, b) => a.Item1.position.CompareTo(b.Item1.position));
            }
            var progress = new Progress(tuples.Sum(t => t.Item1.phones.Length));

            var phrases = tuples.Select(t => t.Item1).ToArray();
            var sources = tuples.Select(t => t.Item2).ToArray();
            var request = tuples.First().Item3;

            var customRenderer = new CustomServerRenderer(serverUrl);
            var httpSemaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new Task<(int index, RenderResult result)>[phrases.Length];

            for (int i = 0; i < phrases.Length; i++) {
                int idx = i;
                var phrase = phrases[idx];

                tasks[idx] = Task.Run(async () => {
                    // ===== Phase 1: 检查缓存 + 生成 JSON (CPU密集, 无限制) =====
                    // 所有 phrase 的 JSON 生成同时跑满 CPU，不受 semaphore 限制
                    string? preJson = null;
                    bool needHttp = false;

                    if (phrase.renderer is CustomServerRenderer) {
                        var wavPath = Path.Join(PathManager.Inst.CachePath, $"custom-{phrase.hash:x16}.wav");
                        if (!File.Exists(wavPath)) {
                            preJson = CustomServerRenderer.ConvertPhraseToJson(phrase);
                            needHttp = true;
                        }
                    }

                    // ===== Phase 2: HTTP 请求 (I/O密集, semaphore限制并发) =====
                    if (needHttp) {
                        await httpSemaphore.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    }
                    try {
                        RenderResult result;
                        if (phrase.renderer is CustomServerRenderer) {
                            result = await customRenderer.RenderImpl(phrase, progress, request.trackNo, cancellation, false, preJson).ConfigureAwait(false);
                        } else {
                            result = await phrase.renderer.Render(phrase, progress, request.trackNo, cancellation, false).ConfigureAwait(false);
                        }
                        return (idx, result);
                    } finally {
                        if (needHttp) {
                            httpSemaphore.Release();
                        }
                    }
                }, cancellation.Token);
            }

            if (playing) {
                // 播放模式：按位置顺序渐进式更新
                var remaining = new List<Task<(int index, RenderResult result)>>(tasks);
                while (remaining.Count > 0 && !cancellation.IsCancellationRequested) {
                    var completedTask = await Task.WhenAny(remaining).ConfigureAwait(false);
                    remaining.Remove(completedTask);
                    var (index, result) = await completedTask.ConfigureAwait(false);
                    sources[index].SetSamples(result.samples);
                }
                if (request.sources.All(s => s.HasSamples)) {
                    request.part.SetMix(request.mix);
                    DocManager.Inst.ExecuteCmd(new PartRenderedNotification(request.part));
                }
            } else {
                // 非播放模式：一次性处理所有结果
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var (index, result) in results) {
                    sources[index].SetSamples(result.samples);
                    // 每次设置后检查是否全部完成（兼容多 track 场景）
                }
                if (request.sources.All(s => s.HasSamples)) {
                    request.part.SetMix(request.mix);
                    DocManager.Inst.ExecuteCmd(new PartRenderedNotification(request.part));
                }
            }
            progress.Clear();
        }
    }
}
