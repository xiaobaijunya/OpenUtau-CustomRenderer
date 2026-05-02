using System;
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

            // === 构建音素列表 ===
            var phonemeList = BuildPhonemeList(phrase);

            // === 自动采样所有曲线参数 ===
            var curves = CustomF0Utils.SampleAllCurves(phrase, frameMs, totalFrames);

            // === 动态参数：每个参数始终保留，如果全为默认值则传 null ===
            var dynamicParam = new Dictionary<string, object?>();

            // 已知的标准曲线，始终保留
            var knownCurves = new[] { "pitd", "genc", "brec", "tenc", "voic" };
            foreach (var abbr in knownCurves) {
                if (curves.TryGetValue(abbr, out var data) && !CustomF0Utils.IsCurveDefault(abbr, data)) {
                    dynamicParam[abbr] = data;
                } else {
                    dynamicParam[abbr] = null;
                }
            }

            // 自定义曲线（来自 phrase.curves）：同样处理
            foreach (var kvp in curves) {
                if (Array.IndexOf(knownCurves, kvp.Key) >= 0) {
                    continue; // 已在上面处理
                }
                dynamicParam[kvp.Key] = CustomF0Utils.IsCurveDefault(kvp.Key, kvp.Value)
                    ? null
                    : kvp.Value;
            }

            object dynamicParameter = dynamicParam;

            var jsonData = new {
                hop_size = hopSize,
                sample_rate = sampleRate,
                frame_ms = frameMs,
                out_wav = Path.Join(PathManager.Inst.CachePath, $"custom-{phrase.hash:x16}.wav"),
                wav_dur = phrase.durationMs,
                phoneme_list = phonemeList,
                Dynamic_parameter = dynamicParameter
            };

            var json = JsonConvert.SerializeObject(jsonData, Formatting.None);
            return json;
        }

        /// <summary>
        /// 构建音素列表，每个音素包含所有 flags 和标准属性。
        /// </summary>
        private static Dictionary<string, object> BuildPhonemeList(RenderPhrase phrase) {
            var phonemeList = new Dictionary<string, object>();

            for (int i = 0; i < phrase.phones.Length; i++) {
                var phone = phrase.phones[i];
                var envelope = phone.envelope;

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
                    }
                };
                phonemeList[(i + 1).ToString()] = phonemeData;
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
