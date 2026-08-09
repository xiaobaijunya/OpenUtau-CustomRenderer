using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.HiFiUtau {
    /// <summary>
    /// 将 RenderPhrase 转换为 hifiutau-engine 所需的音素 JSON。
    /// 完全独立于 CustomRenderer，可单独维护。
    /// </summary>
    public static class HifiUtauPhraseJson {
        /// <summary>
        /// 构建完整音素 JSON（hop_size/frame_ms/phoneme_list/Dynamic_parameter）。
        /// out_wav 默认为 CustomServer 兼容路径，调用方可覆盖。
        /// </summary>
        public static JObject Build(RenderPhrase phrase) {
            // 帧移参数：hop_size=256，对应~5.8ms/帧
            const int hopSize = 256;
            const int sampleRate = 44100;
            double frameMs = 1000.0 * hopSize / sampleRate;

            // 计算目标帧数，考虑leadingMs前导时间
            int totalFrames = (int)Math.Ceiling((phrase.durationMs + phrase.leadingMs) / frameMs);

            // === 全局曲线（供兼容性使用） ===
            var curves = HifiF0Utils.SampleAllCurves(phrase, frameMs, totalFrames);

            // === 动态参数：无论是否默认值，始终写入实际数值 ===
            var dynamicParam = new Dictionary<string, object?>();

            // 已知的标准曲线
            var knownCurves = new[] { "pitd", "genc", "brec", "tenc", "voic", "lowc", "brel", "breh" };
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

            // === 构建逐音素曲线列表（嵌入每个音素） ===
            var perPhonemeCurves = new Dictionary<string, Dictionary<string, double[]>>();
            for (int i = 0; i < phrase.phones.Length; i++) {
                var phone = phrase.phones[i];
                var curvesForPhone = HifiF0Utils.SampleCurvesForPhoneme(
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
                Dynamic_parameter = dynamicParam,
            };

            return JObject.FromObject(jsonData);
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
                    { "phtp", phone.phonemeType },
                    { "strt", phone.stretchMode },
                    { "splc", phone.spliceMode }
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
    }
}
