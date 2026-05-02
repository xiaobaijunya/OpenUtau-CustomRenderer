﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.CustomRender {
    public static class CustomF0Utils {
        private const int Interval = 5;

        /// <summary>
        /// OpenUtau 标准曲线的默认值映射表（abbr -> defaultValue）。
        /// 对应 Format.Ustx.AddDefaultExpressions 中的配置。
        /// 注意：dyn（动态曲线）和 shfc（音高偏移曲线）不作为公共字段暴露，
        /// 需要通过 RenderPhrase.curves 或修改 RenderPhrase 访问。
        /// </summary>
        private static readonly Dictionary<string, double> CurveDefaults = new Dictionary<string, double> {
            { "pitd", 0 },
            { "genc", 0 },
            { "brec", 0 },
            { "tenc", 0 },
            { "voic", 100 },
        };

        /// <summary>
        /// 自动发现 RenderPhrase 中所有曲线并采样到目标帧率。
        /// 返回字典（abbr -> double[]），包含所有标准曲线和自定义曲线。
        /// </summary>
        public static Dictionary<string, double[]> SampleAllCurves(
            RenderPhrase phrase,
            double frameMs,
            int totalFrames) {
            var result = new Dictionary<string, double[]>();

            // 定义标准曲线映射：abbr -> (hasData函数, 采样函数)
            // 注意：dyn（动态曲线）和 shfc（音高偏移曲线）存储在 RenderPhrase 内部，
            // 不作为公共字段暴露，因此无法在此采样。
            // 若需要它们，可在 RenderPhrase 中添加公共访问器。
            var standardCurves = new (string abbr, Func<bool> hasData, Func<int, double> sample)[] {
                ("pitd",
                    () => phrase.pitches != null && phrase.pitches.Length > 0,
                    i => {
                        int ticks = GetTicks(phrase, i, frameMs);
                        int idx = ClampIndex(ticks, phrase.pitches!.Length);
                        return MusicMath.ToneToFreq(phrase.pitches[idx] * 0.01);
                    }),
                ("genc",
                    () => phrase.gender != null && phrase.gender.Length > 0,
                    i => {
                        int ticks = GetTicks(phrase, i, frameMs);
                        return phrase.gender![ClampIndex(ticks, phrase.gender.Length)];
                    }),
                ("brec",
                    () => phrase.breathiness != null && phrase.breathiness.Length > 0,
                    i => {
                        int ticks = GetTicks(phrase, i, frameMs);
                        return phrase.breathiness![ClampIndex(ticks, phrase.breathiness.Length)];
                    }),
                ("tenc",
                    () => phrase.tension != null && phrase.tension.Length > 0,
                    i => {
                        int ticks = GetTicks(phrase, i, frameMs);
                        return phrase.tension![ClampIndex(ticks, phrase.tension.Length)];
                    }),
                ("voic",
                    () => phrase.voicing != null && phrase.voicing.Length > 0,
                    i => {
                        int ticks = GetTicks(phrase, i, frameMs);
                        return phrase.voicing![ClampIndex(ticks, phrase.voicing.Length)];
                    }),
            };

            // 采样所有标准曲线
            foreach (var (abbr, hasData, sample) in standardCurves) {
                if (!hasData()) {
                    continue;
                }
                var curve = new double[totalFrames];
                for (int i = 0; i < totalFrames; i++) {
                    curve[i] = sample(i);
                }
                result[abbr] = curve;
            }

            // 采样自定义曲线（由 renderer 自定义的额外曲线）
            if (phrase.curves != null) {
                foreach (var tuple in phrase.curves) {
                    if (tuple.Item2 == null || tuple.Item2.Length == 0) {
                        continue;
                    }
                    var curve = new double[totalFrames];
                    for (int i = 0; i < totalFrames; i++) {
                        int ticks = GetTicks(phrase, i, frameMs);
                        curve[i] = tuple.Item2[ClampIndex(ticks, tuple.Item2.Length)];
                    }
                    result[tuple.Item1] = curve;
                }
            }

            return result;
        }

        /// <summary>
        /// 判断单条曲线是否全部为默认值。
        /// pitd（音高偏差）永远视为有用，始终返回 false。
        /// 未知曲线回退默认值 0。
        /// </summary>
        public static bool IsCurveDefault(string abbr, double[] curve) {
            if (abbr == "pitd") {
                return false; // pitd 永远有用
            }
            if (curve == null || curve.Length == 0) {
                return true;
            }
            double defaultValue = CurveDefaults.TryGetValue(abbr, out var def) ? def : 0.0;
            foreach (var v in curve) {
                if (v != defaultValue) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 检查所有曲线是否均为默认值。
        /// </summary>
        public static bool AllCurvesDefault(Dictionary<string, double[]> curves) {
            if (curves == null || curves.Count == 0) {
                return true;
            }
            foreach (var kvp in curves) {
                if (!IsCurveDefault(kvp.Key, kvp.Value)) {
                    return false;
                }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetTicks(RenderPhrase phrase, int frameIndex, double frameMs) {
            double posMs = GetFramePositionMs(phrase, frameIndex, frameMs);
            double positionOffset = phrase.position - phrase.leading;
            return (int)(phrase.timeAxis.MsPosToTickPos(posMs) - positionOffset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ClampIndex(int ticks, int length) {
            return Math.Max(0, Math.Min(length - 1, ticks / Interval));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetFramePositionMs(RenderPhrase phrase, int frameIndex, double frameMs) {
            double positionMs = phrase.positionMs + frameIndex * frameMs;

            if (phrase.phones.Length == 0) {
                return positionMs;
            }

            var firstPhone = phrase.phones[0];
            if (firstPhone.envelope.Length >= 2) {
                double preutter = -firstPhone.envelope[0].X;
                if (preutter > 0) {
                    return positionMs - preutter;
                }
            }

            return positionMs;
        }
    }
}
