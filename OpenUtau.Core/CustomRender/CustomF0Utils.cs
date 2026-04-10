﻿﻿using System;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.Core.CustomRender {
    public static class CustomF0Utils {
        private const int Interval = 5;

        public static double[] SampleCurveWithConsonant(
            RenderPhrase phrase,
            float[] curve,
            double defaultValue,
            double frameMs,
            int totalFrames,
            Func<double, double> convert) {
            var result = new double[totalFrames];
            
            if (curve == null || curve.Length == 0) {
                Array.Fill(result, defaultValue);
                return result;
            }

            for (int i = 0; i < totalFrames; i++) {
                double posMs = GetFramePositionMs(phrase, i, frameMs);
                int ticks = phrase.timeAxis.MsPosToTickPos(posMs) - (phrase.position - phrase.leading);
                int index = Math.Max(0, Math.Min(curve.Length - 1, (int)((double)ticks / Interval)));
                result[i] = convert(curve[index]);
            }
            
            return result;
        }

        private static double GetFramePositionMs(RenderPhrase phrase, int frameIndex, double frameMs) {
            // 🔧 Layout已经处理了leadingMs，这里只需要考虑辅音偏移
            double positionMs = phrase.positionMs + frameIndex * frameMs;
            
            if (phrase.phones.Length == 0) {
                return positionMs;
            }

            double consonantOffset = 0;
            var firstPhone = phrase.phones[0];
            if (firstPhone.envelope.Length >= 2) {
                double preutter = -firstPhone.envelope[0].X;
                consonantOffset = Math.Max(0, preutter);
            }

            return positionMs - consonantOffset;
        }

        public static double[] SampleCurveSafe(
            RenderPhrase phrase,
            float[] curve,
            double defaultValue,
            double frameMs,
            int totalFrames,
            Func<double, double> convert) {
            var result = new double[totalFrames];
            
            if (curve == null || curve.Length == 0) {
                Array.Fill(result, defaultValue);
                return result;
            }
            
            for (int i = 0; i < totalFrames; i++) {
                double posMs = phrase.positionMs - phrase.leadingMs + i * frameMs;
                int ticks = phrase.timeAxis.MsPosToTickPos(posMs) - (phrase.position - phrase.leading);
                int index = Math.Max(0, Math.Min(curve.Length - 1, (int)((double)ticks / Interval)));
                result[i] = convert(curve[index]);
            }
            
            return result;
        }

        public static double[] SampleCurveWithPhoneTiming(
            RenderPhrase phrase,
            float[] curve,
            double defaultValue,
            double frameMs,
            int totalFrames,
            Func<double, double> convert) {
            var result = new double[totalFrames];
            
            if (curve == null || curve.Length == 0) {
                Array.Fill(result, defaultValue);
                return result;
            }

            for (int i = 0; i < totalFrames; i++) {
                double posMs = GetFramePositionWithPhoneTiming(phrase, i, frameMs);
                int ticks = phrase.timeAxis.MsPosToTickPos(posMs) - (phrase.position - phrase.leading);
                int index = Math.Max(0, Math.Min(curve.Length - 1, (int)((double)ticks / Interval)));
                result[i] = convert(curve[index]);
            }
            
            return result;
        }

        private static double GetFramePositionWithPhoneTiming(RenderPhrase phrase, int frameIndex, double frameMs) {
            double basePosition = phrase.positionMs - phrase.leadingMs + frameIndex * frameMs;
            
            if (phrase.phones.Length == 0) {
                return basePosition;
            }

            double cumulativeOffset = 0;
            double currentFrameTime = frameIndex * frameMs;
            
            foreach (var phone in phrase.phones) {
                if (phone.envelope.Length >= 5) {
                    double preutter = -phone.envelope[0].X;
                    double overlap = phone.envelope[1].X - phone.envelope[0].X;
                    double consonantLength = Math.Max(0, preutter);
                    
                    if (currentFrameTime < phone.durationMs + consonantLength) {
                        if (currentFrameTime < consonantLength) {
                            return basePosition - consonantLength;
                        } else {
                            return basePosition - consonantLength + overlap;
                        }
                    }
                    
                    cumulativeOffset += consonantLength - overlap;
                }
            }
            
            return basePosition - cumulativeOffset;
        }

        public static double[] SampleCurveWithLeading(
            RenderPhrase phrase,
            float[] curve,
            double defaultValue,
            double frameMs,
            int totalFrames,
            Func<double, double> convert) {
            var result = new double[totalFrames];
            
            if (curve == null || curve.Length == 0) {
                Array.Fill(result, defaultValue);
                return result;
            }

            double leadingMs = phrase.leadingMs;
            
            for (int i = 0; i < totalFrames; i++) {
                double posMs = phrase.positionMs - leadingMs + i * frameMs;
                int ticks = phrase.timeAxis.MsPosToTickPos(posMs) - (phrase.position - phrase.leading);
                int index = Math.Max(0, Math.Min(curve.Length - 1, (int)((double)ticks / Interval)));
                result[i] = convert(curve[index]);
            }
            
            return result;
        }
    }
}
