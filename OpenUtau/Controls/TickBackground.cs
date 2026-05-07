using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using ReactiveUI;

namespace OpenUtau.App.Controls {
    class TickBackground : TemplatedControl {
        private static readonly IDashStyle DashStyle = new ImmutableDashStyle(new double[] { 2, 4 }, 0);

        public static readonly DirectProperty<TickBackground, int> ResolutionProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, int>(
                nameof(Resolution),
                o => o.Resolution,
                (o, v) => o.Resolution = v);
        public static readonly DirectProperty<TickBackground, double> TickWidthProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, double>(
                nameof(TickWidth),
                o => o.TickWidth,
                (o, v) => o.TickWidth = v);
        public static readonly DirectProperty<TickBackground, double> TickOffsetProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, double>(
                nameof(TickOffset),
                o => o.TickOffset,
                (o, v) => o.TickOffset = v);
        public static readonly DirectProperty<TickBackground, int> TickOriginProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, int>(
                nameof(TickOrigin),
                o => o.TickOrigin,
                (o, v) => o.TickOrigin = v);
        public static readonly DirectProperty<TickBackground, int> SnapDivProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, int>(
                nameof(SnapDiv),
                o => o.SnapDiv,
                (o, v) => o.SnapDiv = v);
        public static readonly DirectProperty<TickBackground, ObservableCollection<int>?> SnapTicksProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, ObservableCollection<int>?>(
                nameof(SnapTicks),
                o => o.SnapTicks,
                (o, v) => o.SnapTicks = v);
        public static readonly DirectProperty<TickBackground, bool> ShowBarNumberProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, bool>(
                nameof(ShowBarNumber),
                o => o.ShowBarNumber,
                (o, v) => o.ShowBarNumber = v);
        public static readonly DirectProperty<TickBackground, double> TrackHeightProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, double>(
                nameof(TrackHeight),
                o => o.TrackHeight,
                (o, v) => o.TrackHeight = v);
        public static readonly DirectProperty<TickBackground, double> TrackOffsetProperty =
            AvaloniaProperty.RegisterDirect<TickBackground, double>(
                nameof(TrackOffset),
                o => o.TrackOffset,
                (o, v) => o.TrackOffset = v);

        public int Resolution {
            get => _resolution;
            private set => SetAndRaise(ResolutionProperty, ref _resolution, value);
        }
        // Tick width in pixel.
        public double TickWidth {
            get => _tickWidth;
            private set => SetAndRaise(TickWidthProperty, ref _tickWidth, value);
        }
        public double TickOffset {
            get => _tickOffset;
            private set => SetAndRaise(TickOffsetProperty, ref _tickOffset, value);
        }
        public int TickOrigin {
            get => _tickOrigin;
            private set => SetAndRaise(TickOriginProperty, ref _tickOrigin, value);
        }
        public int SnapDiv {
            get => _snapDiv;
            set => SetAndRaise(SnapDivProperty, ref _snapDiv, value);
        }
        public ObservableCollection<int>? SnapTicks {
            get => _snapTicks;
            set => SetAndRaise(SnapTicksProperty, ref _snapTicks, value);
        }
        public bool ShowBarNumber {
            get => _showBarNumber;
            set => SetAndRaise(ShowBarNumberProperty, ref _showBarNumber, value);
        }
        public double TrackHeight {
            get => _trackHeight;
            set => SetAndRaise(TrackHeightProperty, ref _trackHeight, value);
        }
        public double TrackOffset {
            get => _trackOffset;
            set => SetAndRaise(TrackOffsetProperty, ref _trackOffset, value);
        }

        private int _resolution = 480;
        private double _tickWidth;
        private double _tickOffset;
        private int _tickOrigin;
        private int _snapDiv;
        private ObservableCollection<int>? _snapTicks;
        private bool _showBarNumber;
        private double _trackHeight;
        private double _trackOffset;

        private Pen penBar;
        private Pen penBeatUnit;
        private Pen penDanshed;

        public TickBackground() {
            penBar = new Pen(Foreground, 1);
            penBeatUnit = new Pen(Background, 1);
            penDanshed = new Pen(Background, 1) {
                DashStyle = DashStyle,
            };
            MessageBus.Current.Listen<ThemeChangedEvent>()
                .Subscribe(e => InvalidateVisual());
            MessageBus.Current.Listen<TimeAxisChangedEvent>()
                .Subscribe(e => InvalidateVisual());
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == ForegroundProperty) {
                penBar = new Pen(Foreground, 1);
            }
            if (change.Property == BackgroundProperty) {
                penBeatUnit = new Pen(Background, 1);
                penDanshed = new Pen(Background, 1) {
                    DashStyle = DashStyle,
                };
            }
            if (change.Property == ResolutionProperty ||
                change.Property == TickOriginProperty ||
                change.Property == TickWidthProperty ||
                change.Property == TickOffsetProperty ||
                change.Property == SnapDivProperty ||
                change.Property == TrackHeightProperty ||
                change.Property == TrackOffsetProperty) {
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context) {
            if (TickWidth <= 0) {
                return;
            }
            var project = Core.DocManager.Inst.Project;
            int snapUnit = project.resolution * 4 / SnapDiv;
            while (snapUnit * TickWidth < ViewConstants.MinTicklineWidth) {
                snapUnit *= 2; // Avoid drawing too dense.
            }
            double minLineTick = ViewConstants.MinTicklineWidth / TickWidth;
            double pixelOffset = (TickOffset + TickOrigin) * TickWidth;
            double leftTick = TickOffset + TickOrigin;
            double rightTick = TickOffset + TickOrigin + Bounds.Width / TickWidth;

            project.timeAxis.TickPosToBarBeat(TickOrigin, out int bar, out int beat, out int remainingTicks);
            if (bar > 0) {
                bar--;
            }
            int barTick = project.timeAxis.BarBeatToTickPos(bar, 0);
            SnapTicks?.Clear();
            while (barTick <= rightTick) {
                SnapTicks?.Add(barTick);
                // Bar lines and numbers.
                double x = Math.Round(barTick * TickWidth - pixelOffset) + 0.5;
                double y = -0.5;
                var textLayout = TextLayoutCache.Get((bar + 1).ToString(), ThemeManager.BarNumberBrush, 10);
                using (var state = context.PushTransform(Matrix.CreateTranslation(x + 3, 10))) {
                    textLayout.Draw(context, new Point());
                }
                context.DrawLine(penBar, new Point(x, y), new Point(x, Bounds.Height + 0.5f));
                // Lines between bars.
                var timeSig = project.timeAxis.TimeSignatureAtBar(bar);
                int nextBarTick = project.timeAxis.BarBeatToTickPos(bar + 1, 0);
                int ticksPerBeat = project.resolution * 4 * timeSig.beatPerBar / timeSig.beatUnit;
                int ticksPerLine = snapUnit;
                if (ticksPerBeat < snapUnit) {
                    ticksPerLine = ticksPerBeat;
                } else if (ticksPerBeat % snapUnit != 0) {
                    if (ticksPerBeat > minLineTick) {
                        ticksPerLine = ticksPerBeat;
                    } else {
                        ticksPerLine = nextBarTick - barTick;
                    }
                }
                if (nextBarTick > leftTick) {
                    for (int tick = barTick + ticksPerLine; tick < nextBarTick; tick += ticksPerLine) {
                        SnapTicks?.Add(tick);
                        project.timeAxis.TickPosToBarBeat(tick, out int snapBar, out int snapBeat, out int snapRemainingTicks);
                        var pen = snapRemainingTicks != 0 ? penDanshed : penBeatUnit;
                        x = Math.Round(tick * TickWidth - pixelOffset) + 0.5;
                        y = 24;
                        context.DrawLine(pen, new Point(x, y), new Point(x, Bounds.Height + 0.5f));
                    }
                }
                barTick = nextBarTick;
                bar++;
            }
            SnapTicks?.Add(barTick);

            foreach (var tempo in project.tempos) {
                double x = Math.Round(tempo.position * TickWidth - pixelOffset) + 0.5;
                context.DrawLine(penDanshed, new Point(x, 0), new Point(x, 24));
                var textLayout = TextLayoutCache.Get(tempo.bpm.ToString("#0.00"), ThemeManager.BarNumberBrush, 10);
                using (var state = context.PushTransform(Matrix.CreateTranslation(x + 3, 0))) {
                    textLayout.Draw(context, new Point());
                }
            }

            foreach (var timeSig in project.timeSignatures) {
                int tick = project.timeAxis.BarBeatToTickPos(timeSig.barPosition, 0);
                var barTextLayout = TextLayoutCache.Get((timeSig.barPosition + 1).ToString(), ThemeManager.BarNumberBrush, 10);
                double x = Math.Round(tick * TickWidth - pixelOffset) + 0.5 + barTextLayout.Width + 4;
                var textLayout = TextLayoutCache.Get($"{timeSig.beatPerBar}/{timeSig.beatUnit}", ThemeManager.BarNumberBrush, 10);
                using (var state = context.PushTransform(Matrix.CreateTranslation(x + 3, 10))) {
                    textLayout.Draw(context, new Point());
                }
            }

            // 白键之间的边界虚线（E/F, B/C），使用与竖线相同的颜色但虚线样式
            // 从 y=24 开始（Row 2 时间轴高度），覆盖音符区域
            if (TrackHeight > 0) {
                var boundaryPen = new Pen(Foreground, 1) {
                    DashStyle = new ImmutableDashStyle(new double[] { 6, 4 }, 0),
                };
                int track = (int)TrackOffset;
                double top = TrackHeight * (track - TrackOffset);
                while (top < Bounds.Height) {
                    int tone = ViewConstants.MaxTone - 1 - track;
                    bool isBlack = tone >= 0 && MusicMath.IsBlackKey(tone);
                    bool isCenter = tone >= 0 && MusicMath.IsCenterKey(tone);
                    if (!isBlack && !isCenter) {
                        int nextTrack = track + 1;
                        if (nextTrack < ViewConstants.MaxTone) {
                            int nextTone = ViewConstants.MaxTone - 1 - nextTrack;
                            bool nextIsBlack = nextTone >= 0 && MusicMath.IsBlackKey(nextTone);
                            bool nextIsCenter = nextTone >= 0 && MusicMath.IsCenterKey(nextTone);
                            if (!nextIsBlack && !nextIsCenter) {
                                double lineY = top + TrackHeight - 0.5 + 24;
                                context.DrawLine(boundaryPen,
                                    new Point(0, lineY),
                                    new Point(Bounds.Width, lineY));
                            }
                        }
                    }
                    // Draw line between C (center) and B (white non-center below).
                    if (isCenter) {
                        int nextTrack = track + 1;
                        if (nextTrack < ViewConstants.MaxTone) {
                            int nextTone = ViewConstants.MaxTone - 1 - nextTrack;
                            bool nextIsBlack = nextTone >= 0 && MusicMath.IsBlackKey(nextTone);
                            bool nextIsCenter = nextTone >= 0 && MusicMath.IsCenterKey(nextTone);
                            if (!nextIsBlack && !nextIsCenter) {
                                double lineY = top + TrackHeight - 0.5 + 24;
                                context.DrawLine(boundaryPen,
                                    new Point(0, lineY),
                                    new Point(Bounds.Width, lineY));
                            }
                        }
                    }
                    track++;
                    top += TrackHeight;
                }
            }
        }
    }
}
