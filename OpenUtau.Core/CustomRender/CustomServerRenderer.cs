using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Format;
using OpenUtau.Core.HiFiUtau;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Core.CustomRender {
    public class CustomServerRenderer : IRenderer {
        public string ServerUrl { get; set; } = "http://localhost:8000";
        public string Endpoint { get; set; } = "/synthesize";
        // HttpClient 设计为单例复用，避免为每个请求创建新的连接池
        private static readonly HttpClient sharedHttpClient = new HttpClient {
            Timeout = TimeSpan.FromMinutes(5)
        };

        // 基于 hash 的互斥锁，防止相同内容并发进入临界区
        private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _hashLocks =
            new ConcurrentDictionary<ulong, SemaphoreSlim>();

        // 追踪进行中的 HTTP 任务（key: phrase.hash, value: 正在执行的 HTTP 任务）
        // 即使播放被取消，HTTP 任务也会继续完成并将结果写入缓存文件，
        // 后续相同 hash 的渲染请求可以直接等待已有任务，避免重复提交。
        private static readonly ConcurrentDictionary<ulong, Task<byte[]?>> _inFlightHttpTasks =
            new ConcurrentDictionary<ulong, Task<byte[]?>>();

        public CustomServerRenderer() {
        }

        public CustomServerRenderer(string fullUrl) {
            ParseFullUrl(fullUrl);
        }

        private void ParseFullUrl(string fullUrl) {
            if (string.IsNullOrEmpty(fullUrl)) {
                return;
            }
            try {
                var uri = new Uri(fullUrl);
                ServerUrl = uri.Scheme + "://" + uri.Authority;
                Endpoint = uri.PathAndQuery;
            } catch {
                ServerUrl = fullUrl;
                Endpoint = "/synthesize";
            }
        }

        private string GetFullUrl() {
            return ServerUrl.TrimEnd('/') + '/' + Endpoint.TrimStart('/');
        }

        public USingerType SingerType => USingerType.Classic;

        public bool SupportsRenderPitch => false;

        public bool SupportsExpression(UExpressionDescriptor descriptor) {
            return true;
        }

        public RenderResult Layout(RenderPhrase phrase) {
            return new RenderResult() {
                leadingMs = phrase.leadingMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = phrase.durationMs + phrase.leadingMs,
            };
        }

        // IRenderer 接口实现（5参数）
        public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
            CancellationTokenSource cancellation, bool isPreRender) {
            return RenderImpl(phrase, progress, trackNo, cancellation, isPreRender, null);
        }

        // 内部重载：支持传入预生成的 JSON（CustomRenderEngine 流水线使用）
        internal async Task<RenderResult> RenderImpl(RenderPhrase phrase, Progress progress, int trackNo,
            CancellationTokenSource cancellation, bool isPreRender, string? preGeneratedJson) {
            try {
                string progressInfo =
                    $"Track {trackNo + 1}: CustomServerRenderer \"{string.Join(" ", phrase.phones.Select(p => p.phoneme))}\"";

                var wavPath = Path.Join(PathManager.Inst.CachePath, $"custom-{phrase.hash:x16}.wav");
                phrase.AddCacheFile(wavPath);

                var result = Layout(phrase);

                // ===== 第一层检查：快速路径（无锁） =====
                if (File.Exists(wavPath)) {
                    using (var waveStream = new WaveFileReader(wavPath)) {
                        result.samples = Wave.GetSamples(waveStream.ToSampleProvider().ToMono(1, 0));
                    }

                    if (result.samples != null) {
                        Renderers.ApplyDynamics(phrase, result);
                    }

                    progress.Complete(phrase.phones.Length, progressInfo);
                    return result;
                }

                // ===== 基于 hash 的互斥锁，防止并发重复提交相同内容 =====
                var hashLock = GetOrCreateHashLock(phrase.hash);
                await hashLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try {
                    // ===== 第二层检查：获取锁后再次检查缓存（double-check） =====
                    if (File.Exists(wavPath)) {
                        using (var waveStream = new WaveFileReader(wavPath)) {
                            result.samples = Wave.GetSamples(waveStream.ToSampleProvider().ToMono(1, 0));
                        }
                        if (result.samples != null) {
                            Renderers.ApplyDynamics(phrase, result);
                        }
                        progress.Complete(phrase.phones.Length, progressInfo);
                        return result;
                    }

                    // ===== 获取或创建进行中的 HTTP 任务 =====
                    // 如果已有相同 hash 的 HTTP 任务在执行（例如上一次播放取消后仍在跑），
                    // 直接等待该任务，不重复提交。
                    Task<byte[]?> httpTask;
                    if (_inFlightHttpTasks.TryGetValue(phrase.hash, out var existingTask)) {
                        httpTask = existingTask;
                        Log.Debug($"CustomServerRenderer reusing in-flight HTTP task for hash {phrase.hash:x16}");
                    } else {
                        var jsonData = preGeneratedJson ?? ConvertPhraseToJson(phrase);
                        // 可选：将 JSON 写入文件
                        SaveJsonToFile(jsonData, phrase);
                        // 使用 CancellationToken.None：即使播放被取消，HTTP 请求也继续完成，
                        // 确保后端结果被缓存，避免下次播放重复提交。
                        httpTask = SendToServerAsync(jsonData, CancellationToken.None);
                        if (!_inFlightHttpTasks.TryAdd(phrase.hash, httpTask)) {
                            // 竞态：另一个线程刚好也添加了，使用已有的
                            httpTask = _inFlightHttpTasks[phrase.hash];
                        }
                    }

                    // 等待 HTTP 任务完成（可能被用户取消播放，但 HTTP 任务不受影响继续跑）
                    byte[]? wavData;
                    try {
                        wavData = await httpTask.ConfigureAwait(false);
                    } finally {
                        _inFlightHttpTasks.TryRemove(phrase.hash, out _);
                    }

                    if (wavData != null && wavData.Length > 0) {
                        File.WriteAllBytes(wavPath, wavData);
                        using (var waveStream = new WaveFileReader(wavPath)) {
                            result.samples = Wave.GetSamples(waveStream.ToSampleProvider().ToMono(1, 0));
                        }
                        if (result.samples != null) {
                            Renderers.ApplyDynamics(phrase, result);
                        }
                    } else {
                        Log.Warning("Server returned empty response, using fallback rendering");
                        result = FallbackRender(phrase);
                    }
                } finally {
                    hashLock.Release();
                }

                progress.Complete(phrase.phones.Length, progressInfo);
                return result;
            } catch (Exception e) {
                Log.Error(e, "CustomServerRenderer failed");
                return FallbackRender(phrase);
            }
        }

        public static async Task<RenderResult[]> RenderBatch(
            RenderPhrase[] phrases,
            Progress progress,
            int trackNo,
            CancellationTokenSource cancellation,
            string serverUrl = "http://localhost:8000/synthesize",
            int maxConcurrency = 2) {
            if (phrases == null || phrases.Length == 0) {
                return Array.Empty<RenderResult>();
            }

            var results = new RenderResult[phrases.Length];
            var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new List<Task<(int index, RenderResult result)>>(phrases.Length);

            for (int i = 0; i < phrases.Length; i++) {
                int index = i;
                var phrase = phrases[index];
                
                var task = Task.Run(async () => {
                    await semaphore.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    try {
                        var renderer = new CustomServerRenderer(serverUrl);
                        var result = await renderer.Render(phrase, progress, trackNo, cancellation, false).ConfigureAwait(false);
                        return (index, result);
                    } finally {
                        semaphore.Release();
                    }
                }, cancellation.Token);
                tasks.Add(task);
            }

            var completedTasks = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var (index, result) in completedTasks) {
                results[index] = result;
            }

            return results;
        }

        internal static JObject ConvertPhraseToJObject(RenderPhrase phrase) {
            return HifiUtauPhraseJson.Build(phrase);
        }

        internal static string ConvertPhraseToJson(RenderPhrase phrase) {
            return JsonConvert.SerializeObject(ConvertPhraseToJObject(phrase), Formatting.None);
        }

        private async Task<byte[]?> SendToServerAsync(string jsonData, CancellationToken cancellation) {
            try {
                var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                var fullUrl = GetFullUrl();
                var response = await sharedHttpClient.PostAsync(fullUrl, content, cancellation).ConfigureAwait(false);

                if (response.IsSuccessStatusCode) {
                    Log.Debug($"CustomServerRenderer received {response.Content.Headers.ContentLength ?? 0} bytes");
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                } else {
                    var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Log.Error($"Server returned error: {response.StatusCode}, Content: {errorContent}");
                    return null;
                }
            } catch (Exception e) {
                Log.Error(e, "Failed to send data to server");
                return null;
            }
        }

        private RenderResult FallbackRender(RenderPhrase phrase) {
            var result = Layout(phrase);
            // new float[] 自动初始化为0，无需手动填充
            double totalDurationMs = phrase.durationMs + phrase.leadingMs;
            result.samples = new float[(int)(totalDurationMs * 44.1)];
            return result;
        }

        /// <summary>
        /// 将 JSON 写入缓存目录，便于调试。
        /// </summary>
        private static void SaveJsonToFile(string json, RenderPhrase phrase) {
            try {
                var jsonPath = Path.Join(PathManager.Inst.CachePath, $"custom-{phrase.hash:x16}.json");
                File.WriteAllText(jsonPath, json, Encoding.UTF8);
                Log.Debug($"CustomServerRenderer saved JSON to {jsonPath}");
            } catch (Exception e) {
                Log.Error(e, "Failed to save CustomServer JSON file");
            }
        }

        /// <summary>
        /// 获取或创建基于 phrase.hash 的互斥锁，防止相同内容的并发重复提交。
        /// </summary>
        private static SemaphoreSlim GetOrCreateHashLock(ulong hash) {
            var newLock = new SemaphoreSlim(1, 1);
            var hashLock = _hashLocks.GetOrAdd(hash, newLock);
            // 如果 GetOrAdd 返回了已存在的值，释放我们创建的 newLock
            if (hashLock != newLock) {
                newLock.Dispose();
            }
            return hashLock;
        }

        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            return null!;
        }

        public List<RenderRealCurveResult> LoadRenderedRealCurves(RenderPhrase phrase) {
            return new List<RenderRealCurveResult>(0);
        }

        public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) {
            return new UExpressionDescriptor[] { };
        }

        public override string ToString() => "CUSTOM_SERVER";
        
    } // <-- 添加这一行来关闭类
}
