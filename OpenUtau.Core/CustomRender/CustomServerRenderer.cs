﻿﻿﻿﻿﻿﻿﻿﻿using System;
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
        private readonly string serverUrl;
        private readonly HttpClient httpClient;

        public CustomServerRenderer(string serverUrl = "http://localhost:8000/synthesize") {
            this.serverUrl = serverUrl;
            this.httpClient = new HttpClient();
            this.httpClient.Timeout = TimeSpan.FromMinutes(5);
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
                    // 🔧 使用FileStream确保文件完全写入并刷新缓冲区
                    using (var fileStream = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                        fileStream.Write(wavData, 0, wavData.Length);
                        fileStream.Flush();
                    }
                    // 🔧 短暂延迟确保文件系统完成写入
                    await Task.Delay(10).ConfigureAwait(false);
                    
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

            // 时间相关参数
            // duration_ms: 音素实际时长（毫秒），用于拉伸音素
            // position_ms: 音素在时间轴上的位置（毫秒）
            // Oto参数（用户可调整的最终值）
            // Offset: 左偏移量（音频文件中有效音素的起始位置，毫秒）
            // Consonant: 辅音固定部分长度（毫秒）
            // Cutoff: 右切割点（音频文件中有效音素的结束位置，毫秒）
            // Preutter: 音符开始前的发声时间（毫秒）
            // Overlap: 与前一个音素的重叠时间（毫秒）


            // 各控制点的含义：
            // p0 (包络起点/左线)：
            // X: -preutter 毫秒（音符开始前）【辅音长度】【向前延伸】
            // Y: 0（音量从0开始）

            // p1 (攻击点)：
            // X: p0.X + Math.Max(overlap, 5f) 毫秒【交叉长度】【从头向后延伸】
            // Y: atk * vol / 100f（攻击音量）

            // p2 (稳定点/音符开始)：
            // X: Math.Max(0f, p1.X) 毫秒【音符起始点】
            // Y: vol（目标音量）

            // p3 (衰减开始点)：
            // X: DurationMs - tailIntrude 毫秒【固定线】
            // Y: vol * (1f - dec / 100f)（衰减后的音量）

            // p4 (包络终点/右线)：
            // X: p3.X + tailOverlap 毫秒【结尾衰减起始点】
            // Y: 0（音量衰减到0）

            // 重采样参数：固定hop_size=512，对应时间间隔
            const int hopSize = 512;
            const int sampleRate = 44100;
            double frameMs = 1000.0 * hopSize / sampleRate;

            // 🔧 计算目标帧数，考虑leadingMs前导时间，确保采样范围完整
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

            var jsonData = new {
                out_wav = Path.Join(PathManager.Inst.CachePath, $"custom-{phrase.hash:x16}.wav"),
                wav_dur = phrase.durationMs,
                // render_mode = "normal",
                phoneme_list = phonemeList,
                Dynamic_parameter = new {
                    pitch = CustomF0Utils.SampleCurveWithConsonant(phrase, phrase.pitches, 0, frameMs, totalFrames, x => MusicMath.ToneToFreq(x * 0.01)),
                    gen = CustomF0Utils.SampleCurveWithConsonant(phrase, phrase.gender, 0, frameMs, totalFrames, x => x),
                    dyn = CustomF0Utils.SampleCurveWithConsonant(phrase, phrase.dynamics, 0, frameMs, totalFrames, x => x),
                    tension = CustomF0Utils.SampleCurveWithConsonant(phrase, phrase.tension, 0, frameMs, totalFrames, x => x),
                    breath = CustomF0Utils.SampleCurveWithConsonant(phrase, phrase.breathiness, 0, frameMs, totalFrames, x => x)
                }
            };

            var json = JsonConvert.SerializeObject(jsonData, Formatting.Indented);
            // Log.Information($"Generated JSON (UTF-8): {json}");
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

                var response = await httpClient.PostAsync(serverUrl, content, cancellation.Token).ConfigureAwait(false);

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
