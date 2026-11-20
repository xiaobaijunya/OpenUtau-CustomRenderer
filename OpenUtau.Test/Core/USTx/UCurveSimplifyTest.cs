using System;
using System.Linq;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Test.Core.USTx {
    /// <summary>
    /// Simplify used to be a recursive Douglas-Peucker that re-scanned a whole segment on every
    /// split. For the curves the pitch brush produces (one point every 5 ticks, easily thousands of
    /// points) that was O(n^2) and froze the piano roll right after a stroke, so it is now an
    /// incremental sleeve fit. These tests pin the error bound and guard against a return to a
    /// quadratic implementation.
    /// </summary>
    public class UCurveSimplifyTest {
        static UExpressionDescriptor PitdDescriptor() =>
            new UExpressionDescriptor("pitch deviation (curve)", "pitd", -2400, 2400, 0) { type = UExpressionType.Curve };

        /// <summary>Builds a brush-like curve: one point every 5 ticks.</summary>
        static UCurve BuildCurve(int points, Func<int, int> shapeless) {
            var curve = new UCurve(PitdDescriptor());
            int lastX = 0;
            int lastY = 0;
            for (int i = 0; i < points; i++) {
                int x = i * 5;
                int y = shapeless(i);
                curve.Set(x, y, lastX, lastY);
                lastX = x;
                lastY = y;
            }
            return curve;
        }

        static double MaxError(UCurve curve, (int x, int y)[] original) {
            double maxError = 0;
            foreach (var (x, y) in original) {
                int idx = curve.xs.BinarySearch(x);
                double interpolated;
                if (idx >= 0) {
                    interpolated = curve.ys[idx];
                } else {
                    idx = ~idx;
                    if (idx == 0) {
                        idx = 1;
                    }
                    if (idx >= curve.xs.Count) {
                        idx = curve.xs.Count - 1;
                    }
                    interpolated = MusicMath.Linear(
                        curve.xs[idx - 1], curve.xs[idx], curve.ys[idx - 1], curve.ys[idx], x);
                }
                maxError = Math.Max(maxError, Math.Abs(interpolated - y));
            }
            return maxError;
        }

        const double Tolerance = 1.0;

        [Theory]
        [InlineData("flat")]
        [InlineData("line")]
        [InlineData("zigzag")]
        [InlineData("noisy")]
        public void SimplifyStaysWithinTolerance(string shape) {
            var curve = BuildCurve(4000, shape switch {
                "flat" => _ => 0,
                "line" => i => i * 3,
                "zigzag" => i => i % 2 == 0 ? 400 : -400,
                _ => i => (int)Math.Round(Math.Sin(i * 0.15) * 180 + Math.Sin(i * 1.7) * 35),
            });
            var original = curve.xs.Zip(curve.ys, (x, y) => (x, y)).ToArray();

            curve.Simplify();

            Assert.True(curve.xs.Count >= 2);
            Assert.True(curve.xs.Count <= original.Length);
            Assert.True(MaxError(curve, original) <= Tolerance,
                $"Simplify drifted more than {Tolerance} cent in the {shape} case");
        }

        [Fact]
        public void SimplifyCollapsesStraightCurves() {
            var flat = BuildCurve(2000, _ => 0);
            flat.Simplify();
            Assert.True(flat.xs.Count <= 8, $"flat curve kept {flat.xs.Count} points");

            var line = BuildCurve(2000, i => i * 3);
            line.Simplify();
            Assert.True(line.xs.Count <= 8, $"straight line kept {line.xs.Count} points");
        }

        [Fact]
        public void SimplifyHandlesLargeBrushCurveQuickly() {
            // A long stroke easily reaches thousands of points. The old O(n^2) implementation needed
            // tens of seconds for this input, so a very generous bound still catches a regression.
            int points = 20000;
            var curve = BuildCurve(points, i => i % 2 == 0 ? 400 : -400);
            var original = curve.xs.Zip(curve.ys, (x, y) => (x, y)).ToArray();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            curve.Simplify();
            watch.Stop();

            Assert.True(MaxError(curve, original) <= Tolerance);
            Assert.True(watch.ElapsedMilliseconds < 3000,
                $"Simplify took {watch.ElapsedMilliseconds}ms for {points} points");
        }
    }
}
