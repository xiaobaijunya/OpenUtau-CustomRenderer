using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using K4os.Hash.xxHash;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Core.HiFiUtau {
    /// <summary>
    /// HiFiUTAU Local 渲染器 — 分段合成 + 三级缓存（hifigan / hnsep / final）。
    ///
    /// 管线拆分为三个合成请求（引擎端点 /syn_mel、/syn_hnsep、/syn_post）：
    ///   1. mel 拼接 + 变调(genc) + HiFi-GAN → 缓存 hifiutau/hifigan/{hifiganHash}.wav
    ///   2. HN-SEP 气声/谐波分离            → 缓存 hifiutau/hnsep/{hifiganHash}.{harmonic,noise}.wav
    ///   3. 参数应用                        → 缓存 hifiutau/final/{finalHash}.wav
    ///
    /// 缓存命中规则：
    ///   - final 命中 → 直接使用（最快）
    ///   - 修改旋律/歌词/变调 → 只重算 hifigan（hnsep/final 哈希随 hifigan 变化）
    ///   - 修改参数（但 hifigan 不变）→ hnsep 缓存直接命中，只重算 post
    ///   - 所有 HN-SEP 相关参数均为默认值 → 完全跳过 hnsep 请求
    ///
    /// 完全独立于 CustomRenderer（不引用其任何类型），可单独删除旧渲染器。
    /// </summary>
    public class HifiUtauServerRenderer : IRenderer {
        public string ServerUrl { get; set; } = "http://localhost:8000";

        // HttpClient 单例复用
        private static readonly HttpClient sharedHttpClient = new HttpClient {
            Timeout = TimeSpan.FromMinutes(10)
        };

        // 基于 phrase.hash 的互斥锁，防止相同内容并发重复提交
        private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _hashLocks =
            new ConcurrentDictionary<ulong, SemaphoreSlim>();

        public HifiUtauServerRenderer() {
        }

        public HifiUtauServerRenderer(string fullUrl) {
            if (!string.IsNullOrEmpty(fullUrl)) {
                ServerUrl = fullUrl;
            }
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

        public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
            CancellationTokenSource cancellation, bool isPreRender) {
            return RenderImpl(phrase, progress, trackNo, cancellation, isPreRender);
        }

        internal async Task<RenderResult> RenderImpl(RenderPhrase phrase, Progress progress, int trackNo,
            CancellationTokenSource cancellation, bool isPreRender) {
            string progressInfo =
                $"Track {trackNo + 1}: HifiUtau \"{string.Join(" ", phrase.phones.Select(p => p.phoneme))}\"";
            try {
                var result = Layout(phrase);

                // ── 计算缓存哈希 ──
                ulong hifiganHash = ComputeHifiganHash(phrase);
                ulong finalHash = ComputeFinalHash(phrase, hifiganHash);
                bool needsHnsep = NeedsHnsep(phrase);

                var cacheRoot = Path.Join(PathManager.Inst.CachePath, "hifiutau");
                var hifiganDir = Path.Join(cacheRoot, "hifigan");
                var hnsepDir = Path.Join(cacheRoot, "hnsep");
                var finalDir = Path.Join(cacheRoot, "final");
                Directory.CreateDirectory(hifiganDir);
                Directory.CreateDirectory(hnsepDir);
                Directory.CreateDirectory(finalDir);

                string hifiganPath = Path.Join(hifiganDir, $"{hifiganHash:x16}.wav");
                string harmonicPath = Path.Join(hnsepDir, $"{hifiganHash:x16}.harmonic.wav");
                string noisePath = Path.Join(hnsepDir, $"{hifiganHash:x16}.noise.wav");
                string finalPath = Path.Join(finalDir, $"{finalHash:x16}.wav");
                phrase.AddCacheFile(finalPath);

                // 快路径：final 缓存命中（无锁）
                if (File.Exists(finalPath)) {
                    result.samples = LoadSamples(finalPath);
                    if (result.samples != null) {
                        Renderers.ApplyDynamics(phrase, result);
                    }
                    progress.Complete(phrase.phones.Length, progressInfo);
                    return result;
                }

                // 基于 hash 的互斥锁 + double-check
                var hashLock = GetOrCreateHashLock(phrase.hash);
                await hashLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try {
                    if (File.Exists(finalPath)) {
                        result.samples = LoadSamples(finalPath);
                        if (result.samples != null) {
                            Renderers.ApplyDynamics(phrase, result);
                        }
                        progress.Complete(phrase.phones.Length, progressInfo);
                        return result;
                    }

                    // 基础音素 JSON（一次构建，各段复用）
                    var baseJson = HifiUtauPhraseJson.Build(phrase);

                    bool fallback = false;

                    // ── 分段1: mel 拼接 + 变调 + HiFi-GAN ──
                    if (!File.Exists(hifiganPath)) {
                        var payload = (JObject)baseJson.DeepClone();
                        payload["out_wav"] = hifiganPath;
                        try {
                            var resp = await SendSegmentAsync("syn_mel", payload, CancellationToken.None).ConfigureAwait(false);
                            if (!resp.Written) {
                                File.WriteAllBytes(hifiganPath, resp.Wav);
                            } else if (!File.Exists(hifiganPath)) {
                                throw new IOException("引擎声明本地写入，但 hifigan 缓存文件不存在");
                            }
                        } catch (HttpRequestException ex) when (ex.Message.Contains("404") || ex.Message.Contains("NotFound")) {
                            Log.Warning(ex, "引擎不支持 /syn_mel，回退到旧 /synthesize 完整合成");
                            fallback = true;
                        }
                    }

                    if (!fallback) {
                    // ── 分段2: HN-SEP 气声/谐波分离（仅当需要时） ──
                    if (needsHnsep && (!File.Exists(harmonicPath) || !File.Exists(noisePath))) {
                        var payload = new JObject {
                            ["wav"] = hifiganPath,
                        };
                        var resp = await SendSegmentAsync("syn_hnsep", payload, CancellationToken.None).ConfigureAwait(false);
                        if (!resp.WrittenHarmonic) {
                            File.WriteAllBytes(harmonicPath, resp.Harmonic);
                        }
                        if (!resp.WrittenNoise) {
                            File.WriteAllBytes(noisePath, resp.Noise);
                        }
                    }

                    // ── 分段3: 参数应用 ──
                    var postPayload = (JObject)baseJson.DeepClone();
                    if (needsHnsep) {
                        postPayload["harmonic"] = harmonicPath;
                        postPayload["noise"] = noisePath;
                    } else {
                        postPayload["wav"] = hifiganPath;
                    }
                    postPayload["out_wav"] = finalPath;
                    var postResp = await SendSegmentAsync("syn_post", postPayload, CancellationToken.None).ConfigureAwait(false);
                    if (!postResp.Written) {
                        File.WriteAllBytes(finalPath, postResp.Wav);
                    }
                    } else {
                        // 回退：旧 /synthesize 完整合成
                        var payload = (JObject)baseJson.DeepClone();
                        var wav = await SendFullSynthesizeAsync(payload, CancellationToken.None).ConfigureAwait(false);
                        if (wav != null && wav.Length > 0) {
                            File.WriteAllBytes(finalPath, wav);
                        }
                    }
                } finally {
                    hashLock.Release();
                }

                if (!File.Exists(finalPath)) {
                    throw new IOException("final 缓存文件未生成");
                }
                result.samples = LoadSamples(finalPath);
                if (result.samples != null) {
                    Renderers.ApplyDynamics(phrase, result);
                }
                progress.Complete(phrase.phones.Length, progressInfo);
                return result;
            } catch (Exception e) {
                Log.Error(e, "HifiUtauServerRenderer failed");
                // 失败时也完成进度，避免渲染进度条卡住
                progress.Complete(phrase.phones.Length, progressInfo);
                return FallbackRender(phrase);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  缓存哈希
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// hifigan 缓存哈希：所有影响 HiFi-GAN 之前输入的字段
        /// （音素/oto/vel/vol/phtp/envelope/splc 已含在 phone.hash；另加 F0 与 genc）。
        /// </summary>
        private static ulong ComputeHifiganHash(RenderPhrase phrase) {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(phrase.singer?.Id ?? string.Empty);
                    writer.Write(phrase.renderer?.ToString() ?? string.Empty);
                    writer.Write(phrase.timeAxis.Timestamp);
                    foreach (var phone in phrase.phones) {
                        writer.Write(phone.hash);
                    }
                    WriteFloatArray(writer, phrase.pitches); // F0（含 pitd 偏差）
                    WriteFloatArray(writer, phrase.gender);  // genc
                    return XXH64.DigestOf(stream.ToArray());
                }
            }
        }

        /// <summary>
        /// final 缓存哈希：hifiganHash + 所有影响参数应用阶段的曲线。
        /// dyn 除外（仍由 C# 端 ApplyDynamics 施加，不入缓存键）。
        /// </summary>
        private static ulong ComputeFinalHash(RenderPhrase phrase, ulong hifiganHash) {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(hifiganHash);
                    WriteFloatArray(writer, phrase.breathiness);  // brec
                    WriteFloatArray(writer, phrase.tension);      // tenc
                    WriteFloatArray(writer, phrase.voicing);      // voic
                    WriteFloatArray(writer, phrase.breathLow);    // brel
                    WriteFloatArray(writer, phrase.breathHigh);   // breh
                    WriteFloatArray(writer, phrase.warmth);       // bri
                    WriteFloatArray(writer, phrase.hcmp);         // hcmp
                    WriteFloatArray(writer, phrase.lowcut);       // lowc
                    // growl (gwl) 位于自定义曲线中
                    var growl = phrase.curves?.FirstOrDefault(c => c.Item1 == Format.Ustx.GWL)?.Item2;
                    WriteFloatArray(writer, growl);
                    return XXH64.DigestOf(stream.ToArray());
                }
            }
        }

        /// <summary>
        /// 是否需要 HN-SEP 分离：与引擎侧判定一致。
        /// breath/tension/brel/breh/bri/hcmp 任一处偏离默认 0，或 voicing 偏离默认 100。
        /// </summary>
        private static bool NeedsHnsep(RenderPhrase phrase) {
            return AnyNotClose(phrase.breathiness, 0, 0.5)
                || AnyNotClose(phrase.tension, 0, 0.5)
                || !AllClose(phrase.voicing, 100, 0.05)
                || AnyNotClose(phrase.breathLow, 0, 0.5)
                || AnyNotClose(phrase.breathHigh, 0, 0.5)
                || AnyNotClose(phrase.warmth, 0, 0.5)
                || AnyNotClose(phrase.hcmp, 0, 0.5);
        }

        private static bool AnyNotClose(float[] arr, float value, double atol) {
            if (arr == null || arr.Length == 0) {
                return false;
            }
            foreach (var v in arr) {
                if (Math.Abs(v - value) > atol) {
                    return true;
                }
            }
            return false;
        }

        private static bool AllClose(float[] arr, float value, double rtol) {
            if (arr == null || arr.Length == 0) {
                return true;
            }
            foreach (var v in arr) {
                if (Math.Abs(v - value) > rtol * Math.Abs(value) + 1e-8) {
                    return false;
                }
            }
            return true;
        }

        private static void WriteFloatArray(BinaryWriter writer, float[] array) {
            if (array == null) {
                writer.Write("null");
                return;
            }
            foreach (var v in array) {
                writer.Write(v);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  HTTP 请求
        // ════════════════════════════════════════════════════════════════

        class SegmentResponse {
            public bool Written;          // mel/post: 引擎已本地写入 out_wav
            public byte[] Wav;            // mel/post: 未写入时的 wav 字节
            public bool WrittenHarmonic;  // hnsep
            public bool WrittenNoise;     // hnsep
            public byte[] Harmonic;
            public byte[] Noise;
        }

        /// <summary>POST 分段端点；返回 JSON（written）或 wav 字节（可 gzip）。</summary>
        private async Task<SegmentResponse> SendSegmentAsync(string endpoint, JObject payload, CancellationToken cancellation) {
            var url = ServerUrl.TrimEnd('/') + "/" + endpoint.TrimStart('/');
            var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
            var response = await sharedHttpClient.PostAsync(url, content, cancellation).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) {
                var err = Encoding.UTF8.GetString(bytes);
                throw new HttpRequestException($"Server returned {response.StatusCode}: {err}");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("json")) {
                var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
                var r = new SegmentResponse {
                    Written = json.Value<bool>("written"),
                    WrittenHarmonic = json.Value<bool>("written_harmonic"),
                    WrittenNoise = json.Value<bool>("written_noise"),
                };
                if (!r.WrittenHarmonic) {
                    r.Harmonic = DecodeB64(json.Value<string>("harmonic_b64"));
                }
                if (!r.WrittenNoise) {
                    r.Noise = DecodeB64(json.Value<string>("noise_b64"));
                }
                return r;
            }

            // wav 字节响应（本地合成，直接传原始 wav，不压缩）
            var writtenHeader = GetHeader(response, "X-HiFiUTAU-Written");
            return new SegmentResponse {
                Written = writtenHeader == "true",
                Wav = bytes,
            };
        }

        /// <summary>POST 旧 /synthesize 完整合成；返回最终 wav 字节。</summary>
        private async Task<byte[]> SendFullSynthesizeAsync(JObject payload, CancellationToken cancellation) {
            var url = ServerUrl.TrimEnd('/') + "/synthesize";
            var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
            var response = await sharedHttpClient.PostAsync(url, content, cancellation).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                var err = Encoding.UTF8.GetString(bytes);
                throw new HttpRequestException($"Server returned {response.StatusCode}: {err}");
            }
            return bytes;
        }

        private static string GetHeader(HttpResponseMessage response, string name) {
            // 自定义响应头（X-HiFiUTAU-*）在 HttpResponseMessage.Headers，
            // 已知实体头（Content-Type 等）在 Content.Headers，需同时查找
            if (response.Headers.TryGetValues(name, out var values)) {
                return values.FirstOrDefault();
            }
            if (response.Content.Headers.TryGetValues(name, out values)) {
                return values.FirstOrDefault();
            }
            return null;
        }

        private static byte[] DecodeB64(string b64) {
            if (string.IsNullOrEmpty(b64)) {
                return null;
            }
            return Convert.FromBase64String(b64);
        }

        // ════════════════════════════════════════════════════════════════
        //  其它
        // ════════════════════════════════════════════════════════════════

        private static float[] LoadSamples(string path) {
            using (var waveStream = new WaveFileReader(path)) {
                return Wave.GetSamples(waveStream.ToSampleProvider().ToMono(1, 0));
            }
        }

        private RenderResult FallbackRender(RenderPhrase phrase) {
            var result = Layout(phrase);
            double totalDurationMs = phrase.durationMs + phrase.leadingMs;
            result.samples = new float[(int)(totalDurationMs * 44.1)];
            return result;
        }

        private static SemaphoreSlim GetOrCreateHashLock(ulong hash) {
            var newLock = new SemaphoreSlim(1, 1);
            var hashLock = _hashLocks.GetOrAdd(hash, newLock);
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

        public override string ToString() => "HIFIUTAU_LOCAL";
    }
}
