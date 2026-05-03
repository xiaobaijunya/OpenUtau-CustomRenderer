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
using OpenUtau.Core.DiffSinger;
using OpenUtau.Core.Format;
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

        // 基于 hash 的进程内锁，防止相同内容的并发重复提交
        // key: phrase.hash, value: SemaphoreSlim(1,1) 作为互斥锁
        // 注意：SemaphoreSlim 非常轻量（~200 bytes），一个工程中唯一的 phrase hash 数量有限，
        // 无需主动清理，进程结束后自动回收
        private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _hashLocks =
            new ConcurrentDictionary<ulong, SemaphoreSlim>();

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
                progress.Complete(0, progressInfo);

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
                    // 可能在等待锁期间，另一个线程已完成渲染并写入了缓存文件
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

                    var jsonData = preGeneratedJson ?? ConvertPhraseToJson(phrase);

                    var wavData = await SendToServerAsync(jsonData, cancellation).ConfigureAwait(false);
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
            int maxConcurrency = 4) {
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

        internal static string ConvertPhraseToJson(RenderPhrase phrase) {
            // 帧移参数：hop_size=256，对应~5.8ms/帧
            const int hopSize = 256;
            const int sampleRate = 44100;
            double frameMs = 1000.0 * hopSize / sampleRate;

            // 计算目标帧数，考虑leadingMs前导时间
            int totalFrames = (int)Math.Ceiling((phrase.durationMs + phrase.leadingMs) / frameMs);

            // === 全局曲线（供兼容性使用） ===
            var curves = CustomF0Utils.SampleAllCurves(phrase, frameMs, totalFrames);

            // === 动态参数：无论是否默认值，始终写入实际数值 ===
            var dynamicParam = new Dictionary<string, object?>();

            // 已知的标准曲线
            var knownCurves = new[] { "pitd", "genc", "brec", "tenc", "voic" };
            foreach (var abbr in knownCurves) {
                if (curves.TryGetValue(abbr, out var data)) {
                    dynamicParam[abbr] = data;
                }
            }

            // 自定义曲线
            foreach (var kvp in curves) {
                if (Array.IndexOf(knownCurves, kvp.Key) >= 0) {
                    continue;
                }
                dynamicParam[kvp.Key] = kvp.Value;
            }

            object dynamicParameter = dynamicParam;

            // === 构建逐音素曲线列表（嵌入每个音素） ===
            var perPhonemeCurves = new Dictionary<string, Dictionary<string, double[]>>();
            for (int i = 0; i < phrase.phones.Length; i++) {
                var phone = phrase.phones[i];
                var curvesForPhone = CustomF0Utils.SampleCurvesForPhoneme(
                    phrase, phone, i, frameMs, totalFrames);
                perPhonemeCurves[(i + 1).ToString()] = curvesForPhone;
            }

            // === 构建音素列表（每个音素自带曲线） ===
            var phonemeList = BuildPhonemeList(phrase, perPhonemeCurves);

            var jsonData = new {
                hop_size = hopSize,
                sample_rate = sampleRate,
                frame_ms = frameMs,
                out_wav = Path.Join(PathManager.Inst.CachePath, $"custom-{phrase.hash:x16}.wav"),
                wav_dur = phrase.durationMs,
                phoneme_list = phonemeList,
                Dynamic_parameter = dynamicParameter,
            };

            var json = JsonConvert.SerializeObject(jsonData, Formatting.None);
            return json;
        }

        /// <summary>
        /// 构建音素列表，每个音素包含所有 flags、标准属性和逐音素动态参数曲线。
        /// </summary>
        private static Dictionary<string, object> BuildPhonemeList(
            RenderPhrase phrase,
            Dictionary<string, Dictionary<string, double[]>> perPhonemeCurves) {
            var phonemeList = new Dictionary<string, object>();

            for (int i = 0; i < phrase.phones.Length; i++) {
                var phone = phrase.phones[i];
                var envelope = phone.envelope;
                var key = (i + 1).ToString();

                // 自动收集所有 flags：标准属性 + phoneme.flags（来自项目表达式）
                var noteFlags = new Dictionary<string, object> {
                    { "vel", phone.velocity * 100.0 },
                    { "vol", phone.volume * 100.0 },
                    { "mod", phone.modulation * 100.0 },
                    { "shft", phone.toneShift },
                    { "phtp", phone.phonemeType }
                };
                foreach (var flag in phone.flags) {
                    // flag: Tuple<flagName, int?, abbr>
                    noteFlags[flag.Item1] = flag.Item2 ?? 0;
                }

                float p3X, p3Y, p4X, p4Y;

                if (i < phrase.phones.Length - 1) {
                    var nextPhone = phrase.phones[i + 1];
                    var nextEnvelope = nextPhone.envelope;

                    var nextPreutter = -nextEnvelope[0].X;
                    var nextOverlap = nextEnvelope[1].X - nextEnvelope[0].X;
                    var tailIntrude = Math.Max(nextPreutter, nextPreutter - nextOverlap);
                    var tailOverlap = Math.Max(nextOverlap, 0);

                    p3X = (float)(phone.durationMs - tailIntrude);
                    p4X = (float)(p3X + tailOverlap);
                    p3Y = envelope[3].Y;
                    p4Y = envelope[4].Y;
                } else {
                    p3X = envelope[3].X;
                    p3Y = envelope[3].Y;
                    p4X = envelope[4].X;
                    p4Y = envelope[4].Y;
                }

                // 逐音素动态参数曲线
                Dictionary<string, double[]>? phonemeDynamicParams = null;
                if (perPhonemeCurves.TryGetValue(key, out var curves) && curves != null && curves.Count > 0) {
                    phonemeDynamicParams = curves;
                }

                var phonemeData = new {
                    phoneme_name = phone.phoneme,
                    note_pitch = MusicMath.GetToneName(phone.tone),
                    dur = envelope[4].X,
                    Note_flags = noteFlags,
                    phoneme_oto = new {
                        audio_file_path = phone.oto?.File ?? "",
                        Offset = phone.oto?.Offset ?? 0.0,
                        Consonant = phone.oto?.Consonant ?? 0.0,
                        Cutoff = phone.oto?.Cutoff ?? 0.0,
                        Preutter = phone.oto?.Preutter ?? 0.0,
                        Overlap = phone.oto?.Overlap ?? 0.0,
                    },
                    envelope = new {
                        p0 = new { x = envelope[0].X, y = envelope[0].Y },
                        p1 = new { x = envelope[1].X, y = envelope[1].Y },
                        p2 = new { x = envelope[2].X, y = envelope[2].Y },
                        p3 = new { x = p3X, y = p3Y },
                        p4 = new { x = p4X, y = p4Y }
                    },
                    Dynamic_parameter = phonemeDynamicParams
                };
                phonemeList[key] = phonemeData;
            }

            return phonemeList;
        }

        private async Task<byte[]?> SendToServerAsync(string jsonData, CancellationTokenSource cancellation) {
            try {
                var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                var fullUrl = GetFullUrl();
                var response = await sharedHttpClient.PostAsync(fullUrl, content, cancellation.Token).ConfigureAwait(false);

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
