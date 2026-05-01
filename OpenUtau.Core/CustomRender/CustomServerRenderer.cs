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
        private readonly HttpClient httpClient;

        public CustomServerRenderer() {
            this.httpClient = new HttpClient();
            this.httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        public CustomServerRenderer(string fullUrl) {
            this.httpClient = new HttpClient();
            this.httpClient.Timeout = TimeSpan.FromMinutes(5);
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

        public async Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
            CancellationTokenSource cancellation, bool isPreRender) {
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

                var jsonData = ConvertPhraseToJson(phrase);
                // Log.Information($"Sending JSON to server: {jsonData}");

                var wavData = await SendToServerAsync(jsonData, cancellation).ConfigureAwait(false);
                if (wavData != null && wavData.Length > 0) {
                    // 直接写文件后立即读取，FileStream.Flush + FileShare.Read 确保一致性
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
            string serverUrl = "http://localhost:8000/synthesize", // Deprecated, use ServerUrl and Endpoint instead
            int maxConcurrency = 4) {
            if (phrases == null || phrases.Length == 0) {
                return Array.Empty<RenderResult>();
            }

            var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            var results = new RenderResult[phrases.Length];
            var tasks = new List<Task<(int index, RenderResult result)>>();
            var semaphore = new SemaphoreSlim(maxConcurrency);

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

            httpClient.Dispose();
            return results;
        }

        private string ConvertPhraseToJson(RenderPhrase phrase) {
            var phonemeList = new Dictionary<string, object>();

            // 重采样参数：固定hop_size=512，对应时间间隔
            const int hopSize = 512;
            const int sampleRate = 44100;
            double frameMs = 1000.0 * hopSize / sampleRate;

            // 计算目标帧数，考虑leadingMs前导时间
            int totalFrames = (int)Math.Ceiling((phrase.durationMs + phrase.leadingMs) / frameMs);

            for (int i = 0; i < phrase.phones.Length; i++) {
                var phone = phrase.phones[i];
                var envelope = phone.envelope;
                var noteFlags = new Dictionary<string, object> {
                    { "vel", phone.velocity * 100.0 }
                };
                foreach (var flag in phone.flags) {
                    noteFlags[flag.Item1] = flag.Item2 ?? 0;
                }

                var actualDur = envelope[3].X - envelope[2].X;
                
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
                    // phoneme_type = GetPhonemeType(phone.phoneme),
                    note_pitch = MusicMath.GetToneName(phone.tone),
                    dur = phone.durationMs,
                    actual_dur = actualDur,
                    // duration_ms = phone.durationMs,
                    // position_ms = phone.positionMs,
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

            CustomF0Utils.SampleAllCurves(phrase, frameMs, totalFrames,
                out var pitchCurve, out var genCurve, out var dynCurve,
                out var tensionCurve, out var breathCurve);

            var jsonData = new {
                out_wav = Path.Join(PathManager.Inst.CachePath, $"custom-{phrase.hash:x16}.wav"),
                wav_dur = phrase.durationMs,
                phoneme_list = phonemeList,
                Dynamic_parameter = new {
                    pitch = pitchCurve,
                    gen = genCurve,
                    dyn = dynCurve,
                    tension = tensionCurve,
                    breath = breathCurve
                }
            };

            var json = JsonConvert.SerializeObject(jsonData, Formatting.None);
            return json;
        }

        private string GetPhonemeType(string phoneme) {
            // var vowels = new HashSet<string> { "a", "i", "u", "e", "o", "A", "I", "U", "E", "O", "N" };
            // return vowels.Contains(phoneme) ? "V" : "C";
            return "1";
        }

        private async Task<byte[]> SendToServerAsync(string jsonData, CancellationTokenSource cancellation) {
            try {
                var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
                content.Headers.ContentType.CharSet = "utf-8";
                Log.Information($"Sending JSON to server with UTF-8 encoding");

                var fullUrl = GetFullUrl();
                var response = await httpClient.PostAsync(fullUrl, content, cancellation.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode) {
                    Log.Information($"Server response: {response.StatusCode}");
                    var wavData = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    Log.Information($"Received wav data, size: {wavData.Length} bytes");
                    return wavData;
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
            // 🔧 正确考虑leadingMs前导时间
            double totalDurationMs = phrase.durationMs + phrase.leadingMs;
            result.samples = new float[(int)(totalDurationMs * 44.1)];
            for (int i = 0; i < result.samples.Length; i++) {
                result.samples[i] = 0;
            }

            return result;
        }

        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            return null;
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
