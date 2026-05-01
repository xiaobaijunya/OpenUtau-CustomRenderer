﻿using System;
using System.Runtime.CompilerServices;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.CustomRender {
    public static class CustomF0Utils {
        private const int Interval = 5;

        /// <summary>
        /// 批量采样所有曲线参数（pitch, gender, dynamics, tension, breathiness）一次遍历完成。
        /// 避免多次独立遍历导致重复计算帧位置。
        /// </summary>
        public static void SampleAllCurves(
            RenderPhrase phrase,
            double frameMs,
            int totalFrames,
            out double[] pitch,
            out double[] gen,
            out double[] dyn,
            out double[] tension,
            out double[] breath) {
            pitch = new double[totalFrames];
            gen = new double[totalFrames];
            dyn = new double[totalFrames];
            tension = new double[totalFrames];
            breath = new double[totalFrames];

            bool hasPitch = phrase.pitches != null && phrase.pitches.Length > 0;
            bool hasGender = phrase.gender != null && phrase.gender.Length > 0;
            bool hasDynamics = phrase.dynamics != null && phrase.dynamics.Length > 0;
            bool hasTension = phrase.tension != null && phrase.tension.Length > 0;
            bool hasBreath = phrase.breathiness != null && phrase.breathiness.Length > 0;

            if (!hasPitch && !hasGender && !hasDynamics && !hasTension && !hasBreath) {
                return;
            }

            double positionOffset = phrase.position - phrase.leading;

            for (int i = 0; i < totalFrames; i++) {
                double posMs = GetFramePositionMs(phrase, i, frameMs);
                int ticks = (int)(phrase.timeAxis.MsPosToTickPos(posMs) - positionOffset);
                int index = Math.Max(0, Math.Min(
                    (hasPitch ? phrase.pitches!.Length :
                     hasGender ? phrase.gender!.Length :
                     hasDynamics ? phrase.dynamics!.Length :
                     hasTension ? phrase.tension!.Length :
                     phrase.breathiness!.Length) - 1,
                    (int)((double)ticks / Interval)));

                if (hasPitch) {
                    int idx = Math.Max(0, Math.Min(phrase.pitches!.Length - 1, (int)((double)ticks / Interval)));
                    pitch[i] = MusicMath.ToneToFreq(phrase.pitches[idx] * 0.01);
                }
                if (hasGender) {
                    int idx = Math.Max(0, Math.Min(phrase.gender!.Length - 1, (int)((double)ticks / Interval)));
                    gen[i] = phrase.gender[idx];
                }
                if (hasDynamics) {
                    int idx = Math.Max(0, Math.Min(phrase.dynamics!.Length - 1, (int)((double)ticks / Interval)));
                    dyn[i] = phrase.dynamics[idx];
                }
                if (hasTension) {
                    int idx = Math.Max(0, Math.Min(phrase.tension!.Length - 1, (int)((double)ticks / Interval)));
                    tension[i] = phrase.tension[idx];
                }
                if (hasBreath) {
                    int idx = Math.Max(0, Math.Min(phrase.breathiness!.Length - 1, (int)((double)ticks / Interval)));
                    breath[i] = phrase.breathiness[idx];
                }
            }
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
