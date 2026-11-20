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
    public class CustomRenderEngine : IRenderEngine {
        public static bool ShouldUseCustomRenderEngine(UProject project) {
            if (project == null || project.parts == null) {
                return false;
            }
            
            // 轨道设置里的渲染器实例是同步解析出来的，先按它判断；乐句是异步构建的，
            // 只靠乐句快照会在构建完成前误判成“不用自定义引擎”。
            if (project.tracks.Any(track => track.RendererSettings?.Renderer is CustomServerRenderer)) {
                return true;
            }
            return project.parts
                .Where(part => part is UVoicePart)
                .Cast<UVoicePart>()
                .Any(part => {
                    var request = part.GetRenderRequest();
                    return request != null && request.phrases.Any(p => p.renderer is CustomServerRenderer);
                });
        }
        
        internal static IRenderEngine CreateRenderEngine(UProject project, int startTick = 0, int endTick = -1, int trackNo = -1) {
            if (ShouldUseCustomRenderEngine(project)) {
                return new CustomRenderEngine(project, startTick, endTick, trackNo);
            }
            // 未使用自定义渲染器时返回 null，由调用方回退到内置的 RenderEngine。
            return null;
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
            int maxConcurrency = 0,
            string serverUrl = "http://localhost:8000/synthesize") {
            this.project = project;
            this.startTick = startTick;
            this.endTick = endTick;
            this.trackNo = trackNo;
            this.maxConcurrency = maxConcurrency > 0 ? maxConcurrency
                : (Preferences.Default?.NumRenderThreads).GetValueOrDefault(2);
            this.serverUrl = serverUrl;
        }

        public Tuple<WaveMix, List<Fader>> RenderMixdown(TaskScheduler uiScheduler, ref CancellationTokenSource cancellation, bool wait = false) {
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

        public Tuple<MasterAdapter, List<Fader>> RenderProject(TaskScheduler uiScheduler, ref CancellationTokenSource cancellation) {
            double startMs = project.timeAxis.TickPosToMsPos(startTick);
            var renderMixdownResult = RenderMixdown(uiScheduler, ref cancellation, wait: false);
            var master = new MasterAdapter(renderMixdownResult.Item1);
            master.SetPosition((int)(startMs * 44100 / 1000) * 2);
            return Tuple.Create(master, renderMixdownResult.Item2);
        }

        public List<WaveMix> RenderTracks(TaskScheduler uiScheduler, ref CancellationTokenSource cancellation) {
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
                    RenderRequests(trackRequests, newCancellation).GetAwaiter().GetResult();
                    var mix = new WaveMix(trackRequests.Select(req => req.mix).ToArray());
                    trackMixes.Add(mix);
                }
            }
            return trackMixes;
        }

        public void PreRenderProject(ref CancellationTokenSource cancellation) {
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
            UVoicePart[] parts;
            lock (project) {
                parts = project.parts
                    .Where(part => part is UVoicePart && (trackNo == -1 || part.trackNo == trackNo))
                    .Where(part => !Preferences.Default.SkipRenderingMutedTracks || !project.tracks[part.trackNo].Muted)
                    .Select(part => part as UVoicePart)
                    .ToArray();
            }
            // 上游的乐句改为异步构建（Pipeline.PhraseSource），必须等最新一次构建落地，
            // 否则这里拿到的是旧乐句或空数组，播放/预渲染会没有声音。
            foreach (var part in parts) {
                part.WaitPhraseSource(TimeSpan.FromSeconds(10));
            }
            RenderPartRequest[] requests;
            lock (project) {
                requests = parts
                    .Select(part => part.GetRenderRequest())
                    .Where(request => request != null)
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
            foreach (var req in requests) {
                // 每轮渲染开始时重置该 part 的乐句进度块，渲染过程中逐句点亮。
                if (req.phrases.Length > 0) {
                    var part = req.part;
                    DocManager.Inst.PostOnUIThread(() => DocManager.Inst.ExecuteCmd(new RebuildPhraseBlocksNotification(part)));
                }
            }
            var tuples = requests
                .SelectMany(req => req.phrases
                    .Zip(req.sources, (phrase, source) => Tuple.Create(phrase, source, req)))
                .ToArray();
            if (playing) {
                // 按播放优先排序：
                // 1) 播放位置及之后结束的片段优先（包含正在播放的片段）
                // 2) 播放位置之前结束的片段排后面
                // 两组内各自按 position 升序
                Array.Sort(tuples, (a, b) => {
                    bool aAfterStart = a.Item1.end > startTick;
                    bool bAfterStart = b.Item1.end > startTick;
                    if (aAfterStart != bAfterStart) {
                        return aAfterStart ? -1 : 1;
                    }
                    return a.Item1.position.CompareTo(b.Item1.position);
                });
            }
            var progress = new Progress(tuples.Sum(t => t.Item1.phones.Length));

            var phrases = tuples.Select(t => t.Item1).ToArray();
            var sources = tuples.Select(t => t.Item2).ToArray();

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
                        var phraseRequest = tuples[idx].Item3;
                        inProgress.Add(RenderOnePhrase(
                            idx, phrase, progress, phraseRequest, customRenderer, cancellation));
                    }

                    // 等待任意一个完成（由于按顺序启动，通常靠前的先完成）
                    var completed = await Task.WhenAny(inProgress).ConfigureAwait(false);
                    inProgress.Remove(completed);
                    var (index, result) = await completed.ConfigureAwait(false);
                    sources[index].SetSamples(result.samples);
                    if (!cancellation.IsCancellationRequested) {
                        PublishPhraseResult(tuples[index].Item3, tuples[index].Item1, sources, index, result);
                    }
                }
            } else {
                // ===== 非播放模式：全部并发（导出等场景，顺序不重要） =====
                var httpSemaphore = new SemaphoreSlim(maxConcurrency);
                var tasks = new Task<(int index, RenderResult result)>[phrases.Length];

                for (int i = 0; i < phrases.Length; i++) {
                    int idx = i;
                    var phrase = phrases[idx];
                    var phraseRequest = tuples[idx].Item3;

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
                                idx, phrase, progress, phraseRequest, customRenderer,
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
                    PublishPhraseResult(tuples[index].Item3, tuples[index].Item1, sources, index, result);
                }
            }
            foreach (var request in requests) {
                // 整段都渲染完了：登记完成标记（DAW 取音、缓存判断会用到）。
                if (request.sources != null && request.sources.Length > 0 &&
                    request.sources.All(source => source.HasSamples)) {
                    PlaybackManager.Inst.MixPlanner.MarkPartComplete(
                        request.part, request.phrases.Select(phrase => phrase.hash));
                }
            }
            progress.Clear();
        }

        /// <summary>
        /// 单个 phrase 渲染完成后的公共处理：写入采样、按需更新 part.Mix、通知刷新。
        /// </summary>
        private static void PublishPhraseResult(RenderPartRequest request, RenderPhrase phrase, WaveSource[] sources, int index, RenderResult result) {
            sources[index].SetSamples(result.samples);
            // 把这一句的 PCM 登记到 MixPlanner：波形按句渐进显示，而且跨 pass 保留，
            // 从播放线开始播放时不会再把前面已经渲染好的音频丢掉。
            PlaybackManager.Inst.MixPlanner.RegisterPcm(
                request.part, phrase.hash,
                sources[index].offsetMs, sources[index].estimatedLengthMs, 1, result.samples);
            if (request.ShouldPublishMix()) {
                request.part.SetMix(request.mix);
            }
            // 逐句点亮编曲区的渲染进度块。
            DocManager.Inst.ExecuteCmd(new PhraseRenderStateNotification(request.part, phrase, true));
            DocManager.Inst.ExecuteCmd(new PartRenderedNotification(request.part));
            // 合并后的波形刷新：与内置 RenderEngine 一致，走 10Hz 节流的 WaveformReadyNotification。
            WaveformRefresh.Request();
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
