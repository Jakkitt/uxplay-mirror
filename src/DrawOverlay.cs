using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UxPlayMirror
{
    /// Transparent ink layer laid exactly over the video picture, plus a floating pen palette.
    class DrawOverlay
    {
        readonly Controller c;
        readonly Window ov, bar;
        readonly InkCanvas ink;
        readonly RadioButton toolPen, toolMarker, toolEraser;
        readonly List<Stroke> added = new List<Stroke>();
        Color color = Color.FromRgb(0xFF, 0x3B, 0x30);
        IntPtr ovHwnd, barHwnd;
        int lastX = int.MinValue, lastY, lastW, lastH, barX = int.MinValue, barY;

        public IntPtr Handle { get { return ovHwnd; } }

        static readonly string[] Palette = { "#FF3B30", "#FFCC00", "#34C759", "#0A84FF", "#FFFFFF", "#111111" };
        static readonly string[] PaletteNames = { "แดง", "เหลือง", "เขียว", "น้ำเงิน", "ขาว", "ดำ" };

        public DrawOverlay(Controller controller)
        {
            c = controller;

            ink = new InkCanvas { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
            ink.StrokeCollected += (o, e) => added.Add(e.Stroke);
            ov = new Window
            {
                Title = "UxPlay Mirror Drawing",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Content = ink,
                Left = -10000, Top = -10000, Width = 10, Height = 10
            };
            ov.PreviewKeyDown += (o, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; c.ToggleDrawing(); }
                else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Undo(); }
            };
            ov.SourceInitialized += (o, e) => ovHwnd = new WindowInteropHelper(ov).Handle;

            bar = (Window)Xaml.Load("PenBar.xaml");
            toolPen = (RadioButton)bar.FindName("ToolPen");
            toolMarker = (RadioButton)bar.FindName("ToolMarker");
            toolEraser = (RadioButton)bar.FindName("ToolEraser");
            toolPen.Checked += (o, e) => Apply();
            toolMarker.Checked += (o, e) => Apply();
            toolEraser.Checked += (o, e) => Apply();
            ((Button)bar.FindName("PenUndo")).Click += (o, e) => Undo();
            ((Button)bar.FindName("PenClear")).Click += (o, e) => { ink.Strokes.Clear(); added.Clear(); };
            ((Button)bar.FindName("PenDone")).Click += (o, e) => c.ToggleDrawing();
            foreach (var n in new[] { "PenUndo", "PenClear", "PenDone" })
            {
                var b = (Button)bar.FindName(n);
                System.Windows.Automation.AutomationProperties.SetName(b, (string)b.ToolTip);
            }

            var colors = (StackPanel)bar.FindName("ColorPanel");
            for (int i = 0; i < Palette.Length; i++)
            {
                var col = (Color)ColorConverter.ConvertFromString(Palette[i]);
                var sw = new RadioButton
                {
                    Style = (Style)Application.Current.Resources["Swatch"],
                    Background = new SolidColorBrush(col),
                    ToolTip = PaletteNames[i],
                    IsChecked = i == 0
                };
                System.Windows.Automation.AutomationProperties.SetName(sw, PaletteNames[i]);
                sw.Checked += (o, e) =>
                {
                    color = col;
                    if (toolEraser.IsChecked == true) toolPen.IsChecked = true;
                    Apply();
                };
                colors.Children.Add(sw);
            }
            bar.SourceInitialized += (o, e) =>
            {
                barHwnd = new WindowInteropHelper(bar).Handle;
                long ex = Native.GetWindowLongPtr(barHwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(barHwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                // The palette never shows up in screenshots or recordings.
                Native.SetWindowDisplayAffinity(barHwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            };
            Apply();
        }

        void Apply()
        {
            if (toolEraser.IsChecked == true)
            {
                ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
                return;
            }
            ink.EditingMode = InkCanvasEditingMode.Ink;
            bool marker = toolMarker.IsChecked == true;
            ink.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = color,
                Width = marker ? 18 : 4,
                Height = marker ? 18 : 4,
                IsHighlighter = marker,
                FitToCurve = true,
                IgnorePressure = true,
                StylusTip = marker ? StylusTip.Rectangle : StylusTip.Ellipse
            };
        }

        public void Undo()
        {
            for (int i = added.Count - 1; i >= 0; i--)
            {
                var s = added[i];
                added.RemoveAt(i);
                if (ink.Strokes.Contains(s)) { ink.Strokes.Remove(s); return; }
            }
        }

        public void Open(IntPtr video)
        {
            new WindowInteropHelper(ov).Owner = video;
            ov.Show();
            new WindowInteropHelper(bar).Owner = new WindowInteropHelper(ov).Handle;
            bar.Left = -10000; bar.Top = -10000;
            bar.Show();
            Follow(video);
            ov.Activate();
            ink.Focus();
        }

        public void Close()
        {
            try { bar.Close(); } catch { }
            try { ov.Close(); } catch { }
        }

        /// Keep the ink layer on the video picture and the palette centred at the top.
        public void Follow(IntPtr video)
        {
            if (ovHwnd == IntPtr.Zero || video == IntPtr.Zero) return;
            var r = Native.ClientScreenRect(video);
            if (r.Left != lastX || r.Top != lastY || r.Width != lastW || r.Height != lastH)
            {
                lastX = r.Left; lastY = r.Top; lastW = r.Width; lastH = r.Height;
                Native.SetWindowPos(ovHwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            if (barHwnd == IntPtr.Zero) return;
            Native.RECT b;
            Native.GetWindowRect(barHwnd, out b);
            int x = r.Left + (r.Width - b.Width) / 2, y = r.Top - 6;
            if (x == barX && y == barY) return;
            barX = x; barY = y;
            Native.SetWindowPos(barHwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        public bool HasStrokes { get { return ink.Strokes.Count > 0; } }

        /// Paints the current strokes onto a screenshot of the video picture (same pixel size as the layer).
        public void PaintInto(System.Drawing.Bitmap bmp)
        {
            if (!HasStrokes) return;
            var dpi = VisualTreeHelper.GetDpi(ink);
            var rtb = new RenderTargetBitmap(bmp.Width, bmp.Height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) ink.Strokes.Draw(dc);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                ms.Position = 0;
                using (var img = System.Drawing.Image.FromStream(ms))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.DrawImage(img, 0, 0, bmp.Width, bmp.Height);
            }
        }
    }
}
