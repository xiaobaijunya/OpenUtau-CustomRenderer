using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OpenUtau.App {
    static class ViewConstants {
        public const double TickWidthMax = 256.0 / 480.0;
        public const double TickWidthMin = 4.0 / 480.0;
        public const double TickWidthDefault = 24.0 / 480.0;
        public const double MinTicklineWidth = 12.0;

        public const double TrackHeightMax = 147;
        public const double TrackHeightMin = 42;
        public const double TrackHeightDefault = 105;
        public const double TrackHeightDelta = 21;

        public const double PianoRollTickWidthMax = 640.0 / 480.0;
        public const double PianoRollTickWidthMin = 4.0 / 480.0;
        public const double PianoRollTickWidthDefault = 128.0 / 480.0;
        public const double PianoRollTickWidthShowDetails = 4.0 / 480.0;
        public const double PianoRollMinTicklineWidth = 12.0;

        public const double PianoRollMinHeight = 24;

        public const double NoteHeightMax = 128;
        public const double NoteHeightMin = 8;
        public const double NoteHeightDefault = 22;

        public const int MaxTone = 12 * 11;

        public static readonly Cursor cursorCross = new Cursor(StandardCursorType.Cross);
        public static readonly Cursor cursorHand = new Cursor(StandardCursorType.Hand);
        public static readonly Cursor cursorNo = new Cursor(StandardCursorType.No);
        public static readonly Cursor cursorSizeAll = new Cursor(StandardCursorType.SizeAll);
        public static readonly Cursor cursorSizeNS = new Cursor(StandardCursorType.SizeNorthSouth);
        public static readonly Cursor cursorSizeWE = new Cursor(StandardCursorType.SizeWestEast);

        // Custom tool cursors rendered from SVG path data
        public static readonly Cursor cursorPen = CreateToolCursor(
            "M14.06,9L15,9.94L5.92,19H5V18.08L14.06,9M17.66,3C17.41,3 17.15,3.1 16.96,3.29L15.13,5.12L18.88,8.87L20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18.17,3.09 17.92,3 17.66,3M14.06,6.19L3,17.25V21H6.75L17.81,9.94L14.06,6.19Z",
            0.5, 0.5);
        public static readonly Cursor cursorPenPlus = CreateToolCursor(
            "M14.1,9L15,9.9L5.9,19H5V18.1L14.1,9M17.7,3C17.5,3 17.2,3.1 17,3.3L15.2,5.1L18.9,8.9L20.7,7C21.1,6.6 21.1,6 20.7,5.6L18.4,3.3C18.2,3.1 17.9,3 17.7,3M14.1,6.2L3,17.2V21H6.8L17.8,9.9L14.1,6.2M7,2V5H10V7H7V10H5V7H2V5H5V2H7Z",
            0.5, 0.5);
        public static readonly Cursor cursorKnife = CreateToolCursor(
            "M7.22,11.91C6.89,12.24 6.71,12.65 6.66,13.08L12.17,15.44L20.66,6.96C21.44,6.17 21.44,4.91 20.66,4.13L19.24,2.71C18.46,1.93 17.2,1.93 16.41,2.71L7.22,11.91M5,16V21.75L10.81,16.53L5.81,14.53L5,16M17.12,4.83C17.5,4.44 18.15,4.44 18.54,4.83C18.93,5.23 18.93,5.86 18.54,6.25C18.15,6.64 17.5,6.64 17.12,6.25C16.73,5.86 16.73,5.23 17.12,4.83Z",
            0.5, 0.5);
        public static readonly Cursor cursorEraser = CreateToolCursor(
            "M16.24,3.56L21.19,8.5C21.97,9.29 21.97,10.55 21.19,11.34L12,20.53C10.44,22.09 7.91,22.09 6.34,20.53L2.81,17C2.03,16.21 2.03,14.95 2.81,14.16L13.41,3.56C14.2,2.78 15.46,2.78 16.24,3.56M4.22,15.58L7.76,19.11C8.54,19.9 9.8,19.9 10.59,19.11L14.12,15.58L9.17,10.63L4.22,15.58Z",
            0.5, 0.5);
        public static readonly Cursor cursorDrawPitch = CreateToolCursor(
            "M9.75 20.85C11.53 20.15 11.14 18.22 10.24 17C9.35 15.75 8.12 14.89 6.88 14.06C6 13.5 5.19 12.8 4.54 12C4.26 11.67 3.69 11.06 4.27 10.94C4.86 10.82 5.88 11.4 6.4 11.62C7.31 12 8.21 12.44 9.05 12.96L10.06 11.26C8.5 10.23 6.5 9.32 4.64 9.05C3.58 8.89 2.46 9.11 2.1 10.26C1.78 11.25 2.29 12.25 2.87 13.03C4.24 14.86 6.37 15.74 7.96 17.32C8.3 17.65 8.71 18.04 8.91 18.5C9.12 18.94 9.07 18.97 8.6 18.97C7.36 18.97 5.81 18 4.8 17.36L3.79 19.06C5.32 20 7.88 21.47 9.75 20.85M20.84 5.25C21.06 5.03 21.06 4.67 20.84 4.46L19.54 3.16C19.33 2.95 18.97 2.95 18.76 3.16L17.74 4.18L19.82 6.26M11 10.92V13H13.08L19.23 6.85L17.15 4.77L11 10.92Z",
            0.5, 0.5);

        /// <summary>
        /// Creates a custom Cursor from SVG path data rendered onto a bitmap.
        /// </summary>
        /// <param name="pathData">SVG path geometry data string.</param>
        /// <param name="hotspotXFraction">Hotspot X as fraction of size (0-1), default 0.5.</param>
        /// <param name="hotspotYFraction">Hotspot Y as fraction of size (0-1), default 0.5.</param>
        /// <param name="size">Cursor bitmap size in pixels.</param>
        private static Cursor CreateToolCursor(string pathData, double hotspotXFraction = 0.5, double hotspotYFraction = 0.5, int size = 24) {
            try {
                var geometry = PathGeometry.Parse(pathData);
                var bounds = geometry.Bounds;

                // Scale to fit the bitmap
                double scale = Math.Min(
                    (size - 4) / Math.Max(bounds.Width, 1),
                    (size - 4) / Math.Max(bounds.Height, 1));

                var translateX = -bounds.X * scale + (size - bounds.Width * scale) / 2;
                var translateY = -bounds.Y * scale + (size - bounds.Height * scale) / 2;

                var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
                using (var ctx = bitmap.CreateDrawingContext()) {
                    var pen = new Pen(Brushes.White, 2);
                    var fillBrush = Brushes.White;

                    // Draw outline for visibility
                    var outlineGeo = geometry.Clone();
                    var scaleTransform = new ScaleTransform(scale, scale);
                    var translateTransform = new TranslateTransform(translateX, translateY);
                    var transformGroup = new TransformGroup();
                    transformGroup.Children.Add(scaleTransform);
                    transformGroup.Children.Add(translateTransform);
                    outlineGeo.Transform = transformGroup;
                    ctx.DrawGeometry(null, pen, outlineGeo);

                    // Draw fill
                    var fillGeo = geometry.Clone();
                    fillGeo.Transform = transformGroup;
                    ctx.DrawGeometry(fillBrush, null, fillGeo);
                }

                int hotspotX = (int)(size * hotspotXFraction);
                int hotspotY = (int)(size * hotspotYFraction);
                return new Cursor(bitmap, new PixelPoint(hotspotX, hotspotY));
            } catch {
                // Fallback to default cursor if rendering fails
                return new Cursor(StandardCursorType.Arrow);
            }
        }

        public const int PosMarkerHightlighZIndex = -100;

        public const int ResizeMargin = 8;

        public const int MinTrackCount = 8;
        public const int MinQuarterCount = 256;
        public const int SpareTrackCount = 4;
        public const int SpareQuarterCount = 16;

        public const double TickMinDisplayWidth = 6;
        public const double NoteMinDisplayWidth = 2;

        public const int PartRectangleZIndex = 100;
        public const int PartThumbnailZIndex = 200;
        public const int PartElementZIndex = 200;

        public const int ExpressionHiddenZIndex = 0;
        public const int ExpressionVisibleZIndex = 200;
        public const int ExpressionShadowZIndex = 100;

        public const double ExpHeightMin = 132;
        public const double ExpHeightMax = 600;
    }
}
