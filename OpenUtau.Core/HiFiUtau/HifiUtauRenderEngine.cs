using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core.Render;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Core.HiFiUtau {
    /// <summary>
    /// HiFiUTAU Local 渲染引擎 — 与 CustomRenderEngine 逻辑等价，但完全独立。
    /// 不特判任何具体渲染器，统一调用 phrase.renderer.Render(...)，
    /// 因此删除 CustomRenderer 后此引擎不受影响。
    ///
    /// 通过 CreateRenderEngine 工厂选择：项目使用 HiFiUTAU 渲染器时返回本引擎，
    /// 否则返回 null（由调用方回退到其它引擎）。
    /// </summary>
    public class HifiUtauRenderEngine : IRenderEngine {
        public static bool ShouldUseHifiUtauEngine(UProject project) {
            if (project == null || project.parts == null) {
                return false;
            }
            return project.parts
                .Where(part => part is UVoicePart)
                .Cast<UVoicePart>()
                .Any(part => {
                    var request = part.GetRenderRequest();
                    return request != null && request.phrases.Any(p => p.renderer is HifiUtauServerRenderer);
                });
        }

        internal static IRenderEngine CreateRenderEngine(UProject project, int startTick = 0, int endTick = -1, int trackNo = -1) {
            if (ShouldUseHifiUtauEngine(project)) {
                return new HifiUtauRenderEngine(project, startTick, endTick, trackNo);
            }
            return null;
        }

        readonly UProject project;
        readonly int startTick;
        readonly int endTick;
        readonly int trackNo;
        readonly int maxConcurrency;

        public HifiUtauRenderEngine(
            UProject project,
            int startTick = 0,
            int endTick = -1,
            int trackNo = -1,
            int maxConcurrency = 0) {
            this.project = project;
            this.startTick = startTick;
            this.endTick = endTick;
            this.trackNo = trackNo;
            this.maxConcurrency = maxConcurrency > 0 ? maxConcurrency
                : (Preferences.Default?.NumRenderThreads).GetValueOrDefault(2);
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
            RenderPartRequest[] requests;
            SingerManager.Inst.ReleaseSingersNotInUse(project);
            lock (project) {
                requests = project.parts
                    .Where(part => part is UVoicePart && (trackNo == -1 || part.trackNo == trackNo))
                    .Where(part => !Preferences.Default.SkipRenderingMutedTracks || !project.tracks[part.trackNo].Muted)
                    .Select(part => part as UVoicePart)
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
            var tuples = requests
                .SelectMany(req => req.phrases
                    .Zip(req.sources, (phrase, source) => Tuple.Create(phrase, source, req)))
                .ToArray();
            if (playing) {
                // 按播放优先排序：播放位置及之后结束的片段优先
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

            if (playing) {
                // ===== 播放模式：按位置顺序排队渲染 =====
                var inProgress = new List<Task<(int index, RenderResult result)>>();
                int nextToStart = 0;
                while ((inProgress.Count > 0 || nextToStart < phrases.Length)
                       && !cancellation.IsCancellationRequested) {
                    while (inProgress.Count < maxConcurrency && nextToStart < phrases.Length) {
                        int idx = nextToStart++;
                        var phrase = phrases[idx];
                        var phraseRequest = tuples[idx].Item3;
                        inProgress.Add(RenderOnePhrase(
                            idx, phrase, progress, phraseRequest, cancellation));
                    }
                    var completed = await Task.WhenAny(inProgress).ConfigureAwait(false);
                    inProgress.Remove(completed);
                    var (index, result) = await completed.ConfigureAwait(false);
                    sources[index].SetSamples(result.samples);
                    if (!cancellation.IsCancellationRequested) {
                        PublishPhraseResult(tuples[index].Item3, sources, index, result);
                    }
                }
            } else {
                // ===== 非播放模式：全部并发（导出等场景） =====
                var semaphore = new SemaphoreSlim(maxConcurrency);
                var tasks = new Task<(int index, RenderResult result)>[phrases.Length];
                for (int i = 0; i < phrases.Length; i++) {
                    int idx = i;
                    var phrase = phrases[idx];
                    var phraseRequest = tuples[idx].Item3;
                    tasks[idx] = Task.Run(async () => {
                        await semaphore.WaitAsync(cancellation.Token).ConfigureAwait(false);
                        try {
                            return await RenderOnePhrase(
                                idx, phrase, progress, phraseRequest, cancellation)
                                .ConfigureAwait(false);
                        } finally {
                            semaphore.Release();
                        }
                    }, cancellation.Token);
                }
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var (index, result) in results) {
                    PublishPhraseResult(tuples[index].Item3, sources, index, result);
                }
            }
            progress.Clear();
        }

        /// <summary>单个 phrase 渲染完成后的公共处理。</summary>
        private static void PublishPhraseResult(RenderPartRequest request, WaveSource[] sources, int index, RenderResult result) {
            sources[index].SetSamples(result.samples);
            if (request.ShouldPublishMix()) {
                request.part.SetMix(request.mix);
            }
            DocManager.Inst.ExecuteCmd(new PartRenderedNotification(request.part));
        }

        /// <summary>渲染单个 phrase（统一委托给 phrase.renderer）。</summary>
        private async Task<(int index, RenderResult result)> RenderOnePhrase(
            int idx, RenderPhrase phrase, Progress progress,
            RenderPartRequest request, CancellationTokenSource cancellation) {
            var result = await phrase.renderer.Render(
                phrase, progress, request.trackNo, cancellation, false)
                .ConfigureAwait(false);
            return (idx, result);
        }
    }
}
