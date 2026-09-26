using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SharpCompress;
using YamlDotNet.Serialization;

namespace OpenUtau.Core.Ustx {
    public class UCurve {
        public const int interval = 5;

        [YamlIgnore] public UExpressionDescriptor descriptor;
        public List<int> xs = new List<int>();
        public List<int> ys = new List<int>();
        [YamlIgnore] public List<int> realXs = new List<int>();
        [YamlIgnore] public List<int> realYs = new List<int>();
        public string abbr;

        [YamlIgnore] public bool IsEmpty => xs.Count == 0 || ys.All(y => y == 0);

        public UCurve(UExpressionDescriptor descriptor) {
            Trace.Assert(descriptor != null);
            this.descriptor = descriptor;
            abbr = descriptor.abbr;
        }

        public UCurve() { }

        public UCurve(string abbr) {
            this.abbr = abbr;
        }

        public UCurve Clone() {
            return new UCurve(descriptor) {
                xs = xs.ToList(),
                ys = ys.ToList(),
            };
        }

        public bool IsEmptyBetween(int x0, int x1, int defaultValue) {
            if (Sample(x0) != defaultValue || Sample(x1) != defaultValue) {
                return false;
            }
            int idx = xs.BinarySearch(x0);
            if (idx < 0) {
                idx = ~idx;
            }
            while (idx < xs.Count && xs[idx] <= x1) {
                if (ys[idx] != defaultValue) {
                    return false;
                }
                idx++;
            }
            return true;
        }

        public int Sample(int x) {
            int idx = xs.BinarySearch(x);
            if (idx >= 0) {
                return ys[idx];
            }
            idx = ~idx;
            if (idx > 0 && idx < xs.Count) {
                return (int)Math.Round(MusicMath.Linear(xs[idx - 1], xs[idx], ys[idx - 1], ys[idx], x));
            }
            return (int)descriptor.defaultValue;
        }

        private void Insert(int x, int y) {
            int idx = xs.BinarySearch(x);
            if (idx >= 0) {
                ys[idx] = y;
                return;
            }
            idx = ~idx;
            xs.Insert(idx, x);
            ys.Insert(idx, y);
        }

        public void Set(int x, int y, int lastX, int lastY) {
            x = (int)Math.Round((float)x / interval) * interval;
            lastX = (int)Math.Round((float)lastX / interval) * interval;
            if (x == lastX) {
                int leftY = Sample(x - interval);
                int rightY = Sample(x + interval);
                Insert(x - interval, leftY);
                Insert(x, y);
                Insert(x + interval, rightY);
            } else if (x < lastX) {
                int leftY = Sample(x - interval);
                DeleteBetweenExclusive(x, lastX);
                Insert(x - interval, leftY);
                Insert(x, y);
            } else {
                int rightY = Sample(x + interval);
                DeleteBetweenExclusive(lastX, x);
                Insert(x, y);
                Insert(x + interval, rightY);
            }
        }

        private void DeleteBetweenExclusive(int x1, int x2) {
            int li = xs.BinarySearch(x1);
            if (li >= 0) {
                li++;
            } else {
                li = ~li;
            }
            int ri = xs.BinarySearch(x2);
            if (ri >= 0) {
                ri--;
            } else {
                ri = ~ri - 1;
            }
            if (ri >= li) {
                xs.RemoveRange(li, ri - li + 1);
                ys.RemoveRange(li, ri - li + 1);
            }
        }
        /// <summary>
        /// Drops points that the retained polyline already represents within the tolerance.
        /// The old recursive Douglas-Peucker re-scanned a whole segment on every split, which is
        /// O(n^2) in the worst case — a 8000 point brush curve took ~0.9s (2.7s for a zigzag) and
        /// froze the piano roll right after a pitch stroke, because the brush adds a point every
        /// 5 ticks. This is an incremental sleeve fit: for the current segment it keeps the range of
        /// slopes through the segment start that stay within tolerance of every point seen so far, so
        /// each point costs O(1). Tolerance is measured on the curve value (cents for a pitch curve),
        /// which is what the curve represents.
        /// </summary>
        public void Simplify() {
            if (xs == null || ys == null || xs.Count < 3 || xs.Count != ys.Count) {
                return;
            }
            double tolerance = Math.Min(1, (descriptor.max - descriptor.min) * 0.005);
            // The scan is O(1) per point, the cap only guards against pathological input.
            const int maxSegmentPoints = 65536;
            int count = xs.Count;
            var newXs = new List<int>(count) { xs[0] };
            var newYs = new List<int>(count) { ys[0] };
            int start = 0;
            while (start < count - 1) {
                int end = start + 1;
                double lo = double.NegativeInfinity;
                double hi = double.PositiveInfinity;
                while (end < count - 1 && end - start < maxSegmentPoints) {
                    int x0 = xs[start];
                    int y0 = ys[start];
                    // Point `end` becomes an interior point of the candidate chord start -> end + 1.
                    double dx = xs[end] - x0;
                    if (dx <= 0) {
                        break;
                    }
                    double slope = (ys[end] - y0) / dx;
                    double slack = tolerance / dx;
                    lo = Math.Max(lo, slope - slack);
                    hi = Math.Min(hi, slope + slack);
                    if (lo > hi) {
                        break;
                    }
                    // The candidate chord must itself be within tolerance of the interior points.
                    double nextDx = xs[end + 1] - x0;
                    if (nextDx <= 0) {
                        break;
                    }
                    double nextSlope = (ys[end + 1] - y0) / nextDx;
                    if (nextSlope < lo || nextSlope > hi) {
                        break;
                    }
                    end++;
                }
                newXs.Add(xs[end]);
                newYs.Add(ys[end]);
                start = end;
            }
            xs = newXs;
            ys = newYs;
        }
        public static List<UCurve> MergeCurves(params List<UCurve>[] merging) {
            var merged = new Dictionary<UExpressionDescriptor, UCurve>();
            foreach (var curves in merging) {
                foreach (var curve in curves) {
                    if (curve.descriptor == null) continue;
                    if (!merged.TryGetValue(curve.descriptor, out var existing)) {
                        merged[curve.descriptor] = curve.Clone();
                    } else {
                        // Merge xs and ys, keeping them sorted by xs
                        var xs = existing.xs.Concat(curve.xs).ToList();
                        var ys = existing.ys.Concat(curve.ys).ToList();
                        var zipped = xs.Zip(ys, (x, y) => (x, y)).ToList();
                        zipped.Sort((a, b) => a.x.CompareTo(b.x));
                        existing.xs = zipped.Select(z => z.x).ToList();
                        existing.ys = zipped.Select(z => z.y).ToList();
                    }
                }
            }
            return merged.Values.ToList();
        }
    }

    public class CurveSelection {
        public string? Abbr { get; private set; }
        public (int x, int y) StartPoint { get; set; } = (0, 0);
        public (int x, int y) EndPoint { get; set; } = (0, 0);
        private List<int> xs = new List<int>(); // tick from part start
        private List<int> ys = new List<int>();

        public CurveSelection() { }

        public bool HasValue(string? abbr = null) {
            return Abbr != null && (abbr == null || Abbr == abbr);
        }

        public void Clear() {
            Abbr = null;
            StartPoint = (0, 0);
            EndPoint = (0, 0);
            xs.Clear();
            ys.Clear();
        }

        public void Add (string abbr, (int x, int y) startPoint, (int x, int y) endPoint, IEnumerable<int> xs, IEnumerable<int> ys) {
            Abbr = abbr;
            StartPoint = startPoint;
            EndPoint = endPoint;
            this.xs.AddRange(xs);
            this.ys.AddRange(ys);
        }

        public void GetWholeCurveAndSelection(string abbr, UCurve? curve, out List<int> wholeXs, out List<int> wholeYs) {
            wholeXs = new List<int>();
            wholeYs = new List<int>();
            if (curve != null) {
                wholeXs.AddRange(curve.xs);
                wholeYs.AddRange(curve.ys);
            }
            if (HasValue(abbr)) {
                bool flag = false;
                for (int i = 0; i < wholeXs.Count; i++) {
                    int x = wholeXs[i];
                    if (StartPoint.x < x) {
                        wholeXs.Insert(i, StartPoint.x);
                        wholeYs.Insert(i, StartPoint.y);
                        flag = true;
                        break;
                    }
                }
                if (!flag) {
                    wholeXs.Add(StartPoint.x);
                    wholeYs.Add(StartPoint.y);
                }

                if (StartPoint.x != EndPoint.x) {
                    flag = false;
                    for (int i = 0; i < wholeXs.Count; i++) {
                        int x = wholeXs[i];
                        if (EndPoint.x < x) {
                            wholeXs.Insert(i, EndPoint.x);
                            wholeYs.Insert(i, EndPoint.y);
                            flag = true;
                            break;
                        }
                    }
                    if (!flag) {
                        wholeXs.Add(EndPoint.x);
                        wholeYs.Add(EndPoint.y);
                    }
                }
            }
        }

        public void GetSelectedRange(string abbr, out List<int> xs, out List<int> ys) {
            xs = new List<int>();
            ys = new List<int>();
            if (!HasValue(abbr)) {
                return;
            }
            xs.Add(StartPoint.x);
            ys.Add(StartPoint.y);
            xs.AddRange(this.xs);
            ys.AddRange(this.ys);
            xs.Add(EndPoint.x);
            ys.Add(EndPoint.y);
        }

        public CurveSelection Clone() {
            return new CurveSelection() {
                Abbr = Abbr,
                StartPoint = StartPoint,
                EndPoint = EndPoint,
                xs = new List<int>(xs),
                ys = new List<int>(ys)
            };
        }
    }
}
