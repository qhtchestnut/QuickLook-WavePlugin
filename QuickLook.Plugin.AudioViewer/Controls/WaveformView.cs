using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickLook.Plugin.AudioViewer.Audio;

namespace QuickLook.Plugin.AudioViewer.Controls
{
    /// <summary>
    /// Draws the whole file's waveform, one lane per channel, always fitted to the element.
    /// </summary>
    /// <remarks>
    /// The envelope is rasterized once per (data, size, colour) into a frozen bitmap and
    /// only blitted afterwards, so painting the playhead never re-rasterizes anything —
    /// the playhead is a separate element on top of this one.
    /// </remarks>
    public sealed class WaveformView : FrameworkElement
    {
        private WaveformData _data;
        private Color _waveColor = Color.FromRgb(0xC8, 0xC8, 0xC8);

        /// <summary>
        /// Waveform columns per device pixel. 1.0 keeps every screen pixel a distinct column;
        /// slightly below that trades a little horizontal detail for a calmer, cheaper render.
        /// </summary>
        private const double ColumnDensity = 0.8;

        private ImageSource _bitmap;
        private int _generation;

        // Rebuild key: the cached bitmap is only valid for this data reference, lane pixel
        // height, column count and colour. Kept separately from the drawn bitmap so an
        // in-flight rebuild is not requested twice for the same key.
        private WaveformData _requestedData;
        private int _requestedLanePx = -1;
        private int _requestedColumns = -1;
        private Color _requestedColor;

        public WaveformData Data
        {
            get => _data;
            set
            {
                if (ReferenceEquals(_data, value)) return;
                _data = value;
                InvalidateBitmap();
            }
        }

        /// <summary>Colour of the wave itself; the background is transparent.</summary>
        public Color WaveColor
        {
            get => _waveColor;
            set
            {
                if (value == _waveColor) return;
                _waveColor = value;
                InvalidateBitmap();
            }
        }

        private void InvalidateBitmap()
        {
            _bitmap = null;
            _requestedData = null;
            _requestedLanePx = -1;
            _requestedColumns = -1;
            _generation++;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            var data = _data;
            // Nothing to draw: stay transparent so the host's own background shows through.
            if (data == null || data.Columns == 0 || w <= 1 || h <= 1) return;

            int channels = Math.Max(1, data.ChannelCount);
            var dpi = VisualTreeHelper.GetDpi(this);
            int lanePixelHeight = Math.Max(1, (int)Math.Round(h / channels * dpi.DpiScaleY));
            // One column per device pixel, but never more columns than the scan has.
            // Columns to rasterize, as a fraction of the element's device pixels. Below 1 the
            // envelope is drawn a little coarser than the screen can resolve: one column spans
            // more than a pixel, which reads as a softer waveform and costs proportionally less
            // to reduce and paint. Raise it back towards 1 for the crispest possible edge.
            int columns = Math.Min(data.Columns,
                Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX * ColumnDensity)));

            if (!ReferenceEquals(_requestedData, data)
                || _requestedLanePx != lanePixelHeight
                || _requestedColumns != columns
                || _requestedColor != _waveColor)
            {
                _requestedData = data;
                _requestedLanePx = lanePixelHeight;
                _requestedColumns = columns;
                _requestedColor = _waveColor;

                int generation = ++_generation;
                var source = data;
                int lanePx = lanePixelHeight;
                int waveArgb = ToArgb(_waveColor);

                Task.Run(() =>
                {
                    // Peak-exact reduction to the on-screen column count, so a transient can
                    // never be dropped by the stretch the GPU would otherwise apply.
                    var fitted = source.Downsample(columns);
                    var pixels = WaveformRasterizer.RenderPixels(fitted, lanePx, waveArgb, 0);
                    var bitmap = WaveformRasterizer.ToBitmap(pixels, fitted.Columns, fitted.ChannelCount * lanePx);
                    bitmap.Freeze();
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (generation != _generation) return;   // superseded by a newer rebuild
                        _bitmap = bitmap;
                        InvalidateVisual();
                    }));
                });
            }

            if (_bitmap != null)
                dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
        }

        private static int ToArgb(Color c) => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
    }
}
