using OpenTK;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PolyHubNet
{
    public enum RessSpatialViewMode
    {
        Grid = 0,
        Topography = 1
    }

    public enum RessSpatialComponent
    {
        BaseFrequency = 0,
        SecondHarmonic = 1
    }

    public sealed class RessSpatialGlView : IDisposable
    {
        private sealed class TopographyElectrode
        {
            public readonly string Name;
            public readonly float X;
            public readonly float Y;

            public TopographyElectrode(string name, float sourceX, float sourceY)
            {
                Name = name;
                X = sourceX / 1023.0f;
                Y = sourceY / 1026.0f;
            }
        }

        // Coordinates are taken from the actiCAP 10-10 layout used by the project.
        // They are normalized from the original 1023 x 1026 map.
        private static readonly TopographyElectrode[] TopographyElectrodes =
        {
            new TopographyElectrode("Fp1", 396.6f, 199.0f),
            new TopographyElectrode("Fp2", 618.5f, 199.0f),

            new TopographyElectrode("AF7", 296.0f, 251.2f),
            new TopographyElectrode("AF3", 396.5f, 268.1f),
            new TopographyElectrode("AFz", 507.5f, 272.2f),
            new TopographyElectrode("AF4", 618.4f, 268.0f),
            new TopographyElectrode("AF8", 718.6f, 251.2f),

            new TopographyElectrode("F7", 218.3f, 329.1f),
            new TopographyElectrode("F5", 284.0f, 347.0f),
            new TopographyElectrode("F3", 357.5f, 354.4f),
            new TopographyElectrode("F1", 433.7f, 359.1f),
            new TopographyElectrode("Fz", 507.5f, 362.1f),
            new TopographyElectrode("F2", 582.5f, 359.1f),
            new TopographyElectrode("F4", 658.6f, 354.3f),
            new TopographyElectrode("F6", 730.6f, 347.0f),
            new TopographyElectrode("F8", 798.4f, 329.2f),

            new TopographyElectrode("FT9", 78.5f, 399.4f),
            new TopographyElectrode("FT7", 165.5f, 429.4f),
            new TopographyElectrode("FC5", 252.5f, 437.2f),
            new TopographyElectrode("FC3", 337.8f, 446.2f),
            new TopographyElectrode("FC1", 421.8f, 452.3f),
            new TopographyElectrode("FCz", 507.7f, 448.7f),
            new TopographyElectrode("FC2", 594.5f, 452.2f),
            new TopographyElectrode("FC4", 678.4f, 446.1f),
            new TopographyElectrode("FC6", 765.4f, 441.4f),
            new TopographyElectrode("FT8", 849.3f, 429.4f),
            new TopographyElectrode("FT10", 937.6f, 399.5f),

            new TopographyElectrode("T7", 147.5f, 540.3f),
            new TopographyElectrode("C5", 236.3f, 540.3f),
            new TopographyElectrode("C3", 327.5f, 540.3f),
            new TopographyElectrode("C1", 415.7f, 540.3f),
            new TopographyElectrode("Cz", 507.5f, 540.2f),
            new TopographyElectrode("C2", 598.7f, 540.2f),
            new TopographyElectrode("C4", 688.6f, 540.2f),
            new TopographyElectrode("C6", 780.4f, 540.3f),
            new TopographyElectrode("T8", 867.4f, 540.3f),

            new TopographyElectrode("TP9", 78.4f, 681.5f),
            new TopographyElectrode("TP7", 165.5f, 651.3f),
            new TopographyElectrode("CP5", 249.5f, 639.4f),
            new TopographyElectrode("CP3", 337.8f, 635.2f),
            new TopographyElectrode("CP1", 421.7f, 630.4f),
            new TopographyElectrode("CPz", 507.5f, 629.2f),
            new TopographyElectrode("CP2", 594.5f, 630.4f),
            new TopographyElectrode("CP4", 678.4f, 635.1f),
            new TopographyElectrode("CP6", 765.4f, 639.3f),
            new TopographyElectrode("TP8", 849.4f, 651.3f),
            new TopographyElectrode("TP10", 937.5f, 681.5f),

            new TopographyElectrode("P7", 218.3f, 752.0f),
            new TopographyElectrode("P5", 284.0f, 735.2f),
            new TopographyElectrode("P3", 355.9f, 728.2f),
            new TopographyElectrode("P1", 433.8f, 723.2f),
            new TopographyElectrode("Pz", 507.5f, 720.3f),
            new TopographyElectrode("P2", 582.5f, 723.3f),
            new TopographyElectrode("P4", 658.7f, 728.1f),
            new TopographyElectrode("P6", 730.6f, 735.3f),
            new TopographyElectrode("P8", 798.4f, 752.0f),

            new TopographyElectrode("PO7", 296.0f, 831.4f),
            new TopographyElectrode("PO3", 396.5f, 815.4f),
            new TopographyElectrode("POz", 507.4f, 809.3f),
            new TopographyElectrode("PO4", 618.4f, 815.4f),
            new TopographyElectrode("PO8", 718.5f, 831.6f),

            new TopographyElectrode("O1", 396.6f, 882.2f),
            new TopographyElectrode("Oz", 507.5f, 900.3f),
            new TopographyElectrode("O2", 618.5f, 882.3f),

            new TopographyElectrode("Iz", 507.5f, 988.8f)
        };

        private readonly GLControl _glCtrl;

        private bool _showSpectra;
        private float _spectrumScaleVolts = 5e-6f;
        private double _spectrumMaxFrequency = TimeFrequencySpectrum.MaxFrequency;
        private int _zoomedSpectrumChannel = -1;

        public void SetSpectrumScaleVolts(float scaleVolts)
        {
            if (scaleVolts <= 0 || float.IsNaN(scaleVolts) || float.IsInfinity(scaleVolts))
                return;
            _spectrumScaleVolts = scaleVolts;
            RequestRedraw();
        }
        private float[] _spectra;
        private string[] _spectrumChannelNames = new string[0];
        private string _spectrumStatus = "Select a recorded class";
        private string _spectrumWindowText = "";

        public void SetSpectrumMode(bool enabled)
        {
            if (_showSpectra != enabled)
                _zoomedSpectrumChannel = -1;
            _showSpectra = enabled;
            RequestRedraw();
        }

        public void SetSpectra(float[] spectra, string[] channelNames, string status, int sampleRate = 0, string windowText = null)
        {
            _spectra = spectra;
            if (spectra != null && sampleRate > 1)
                _spectrumMaxFrequency = Math.Min(TimeFrequencySpectrum.MaxFrequency, sampleRate * 0.5);
            _spectrumChannelNames = channelNames ?? new string[0];
            _spectrumStatus = status;
            _spectrumWindowText = windowText ?? "";
            if (spectra != null && _zoomedSpectrumChannel >= _spectrumChannelNames.Length)
                _zoomedSpectrumChannel = -1;
            RequestRedraw();
        }

        private SsvepModel _model;
        private int? _selectedClassId;
        private RessSpatialViewMode _viewMode = RessSpatialViewMode.Grid;
        private RessSpatialComponent _component = RessSpatialComponent.BaseFrequency;

        private bool _glInitialized;
        private bool _textureDirty = true;
        private int _textureId;
        private Bitmap _bitmap;

        public RessSpatialGlView(GLControl glCtrl)
        {
            if (glCtrl == null)
                throw new ArgumentNullException("glCtrl");

            _glCtrl = glCtrl;
            _glCtrl.Paint += GlCtrl_Paint;
            _glCtrl.Resize += GlCtrl_Resize;
            _glCtrl.MouseDoubleClick += GlCtrl_MouseDoubleClick;
        }

        public void SetModel(SsvepModel model)
        {
            _model = model;
            RequestRedraw();
        }

        public void SetSelectedClassId(int? classId)
        {
            _selectedClassId = classId;
            RequestRedraw();
        }

        public void SetViewMode(RessSpatialViewMode viewMode)
        {
            if (_viewMode == viewMode)
                return;

            _viewMode = viewMode;
            RequestRedraw();
        }

        public void SetComponent(RessSpatialComponent component)
        {
            if (_component == component)
                return;

            _component = component;
            RequestRedraw();
        }

        private void RequestRedraw()
        {
            _textureDirty = true;

            if (!_glCtrl.IsDisposed && _glCtrl.IsHandleCreated)
                _glCtrl.Invalidate();
        }

        private void GlCtrl_MouseDoubleClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (!_showSpectra || _spectra == null || e.Button != System.Windows.Forms.MouseButtons.Left)
                return;
            if (_zoomedSpectrumChannel >= 0)
            {
                _zoomedSpectrumChannel = -1;
                RequestRedraw();
                return;
            }
            int channel = HitTestSpectrumChannel(_glCtrl.ClientSize.Width, _glCtrl.ClientSize.Height, e.Location);
            if (channel < 0)
                return;
            _zoomedSpectrumChannel = channel;
            RequestRedraw();
        }

        private int HitTestSpectrumChannel(int width, int height, Point location)
        {
            int channels = _spectrumChannelNames.Length;
            for (int ch = 0; ch < channels; ch++)
                if (GetSpectrumCellBounds(width, height, ch, channels).Contains(location))
                    return ch;
            return -1;
        }

        private static RectangleF GetSpectrumCellBounds(int width, int height, int item, int count)
        {
            int columns = Math.Min(7, count);
            int rows = (count + columns - 1) / columns;
            float cellWidth = Math.Max(1, (width - 32f - 5f * (columns - 1)) / columns);
            float cellHeight = Math.Max(1, (height - 48f - 5f * (rows - 1)) / rows);
            return new RectangleF(16 + (item % columns) * (cellWidth + 5),
                32 + (item / columns) * (cellHeight + 5), cellWidth, cellHeight);
        }

        private void GlCtrl_Resize(object sender, EventArgs e)
        {
            RequestRedraw();
        }

        private void GlCtrl_Paint(object sender, System.Windows.Forms.PaintEventArgs e)
        {
            if (_glCtrl.IsDisposed || !_glCtrl.IsHandleCreated)
                return;

            _glCtrl.MakeCurrent();

            if (!_glInitialized)
            {
                GL.ClearColor(Color.Black);
                GL.Disable(EnableCap.DepthTest);
                GL.Disable(EnableCap.CullFace);
                _glInitialized = true;
            }

            int width = Math.Max(_glCtrl.ClientSize.Width, 1);
            int height = Math.Max(_glCtrl.ClientSize.Height, 1);

            GL.Viewport(0, 0, width, height);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0.0, width, height, 0.0, -1.0, 1.0);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            GL.Clear(ClearBufferMask.ColorBufferBit);

            if (_textureDirty || _textureId == 0)
                RebuildTexture(width, height);

            if (_textureId != 0)
            {
                GL.Enable(EnableCap.Texture2D);
                GL.BindTexture(TextureTarget.Texture2D, _textureId);
                GL.Color4(1.0f, 1.0f, 1.0f, 1.0f);

                GL.Begin(PrimitiveType.Quads);
                GL.TexCoord2(0.0f, 0.0f); GL.Vertex2(0.0f, 0.0f);
                GL.TexCoord2(1.0f, 0.0f); GL.Vertex2(width, 0.0f);
                GL.TexCoord2(1.0f, 1.0f); GL.Vertex2(width, height);
                GL.TexCoord2(0.0f, 1.0f); GL.Vertex2(0.0f, height);
                GL.End();

                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.Disable(EnableCap.Texture2D);
            }

            _glCtrl.SwapBuffers();
        }

        private void RebuildTexture(int width, int height)
        {
            if (!_glInitialized)
                return;

            if (_bitmap != null)
            {
                _bitmap.Dispose();
                _bitmap = null;
            }

            _bitmap = BuildBitmap(width, height);

            if (_textureId == 0)
                _textureId = GL.GenTexture();

            GL.BindTexture(TextureTarget.Texture2D, _textureId);

            BitmapData data = _bitmap.LockBits(
                new Rectangle(0, 0, _bitmap.Width, _bitmap.Height),
                ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    _bitmap.Width,
                    _bitmap.Height,
                    0,
                    OpenTK.Graphics.OpenGL.PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    data.Scan0);
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            _textureDirty = false;
        }

        private Bitmap BuildBitmap(int width, int height)
        {
            Bitmap bitmap = new Bitmap(
                Math.Max(width, 1),
                Math.Max(height, 1),
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(bitmap))
            using (Font titleFont = new Font("Segoe UI", 10.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (Font sectionFont = new Font("Segoe UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (Font channelFont = new Font("Segoe UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (Font valueFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (Font mapFont = new Font("Segoe UI", 7.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (Font smallFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush textBrush = new SolidBrush(Color.White))
            using (Brush dimTextBrush = new SolidBrush(Color.FromArgb(175, 175, 175)))
            using (Pen cellBorderPen = new Pen(Color.FromArgb(90, 90, 90), 1.0f))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                const float margin = 16.0f;
                const float titleY = 10.0f;
                const float contentTop = 42.0f;

                if (_showSpectra)
                {
                    DrawSpectrumGrid(g, width, height, channelFont, smallFont, textBrush, dimTextBrush, cellBorderPen);
                    return bitmap;
                }

                g.DrawString("RESS spatial weights", titleFont, textBrush, margin, titleY);

                if (_model == null)
                {
                    DrawCenteredText(g, "SSVEP model not loaded", width, height, dimTextBrush, sectionFont);
                    return bitmap;
                }

                int classIndex = GetClassIndexById(_selectedClassId);
                if (classIndex < 0)
                {
                    DrawCenteredText(g, "Select an SSVEP class", width, height, dimTextBrush, sectionFont);
                    return bitmap;
                }

                int harmonicIndex = (int)_component;
                if (harmonicIndex < 0 || harmonicIndex >= _model.HarmonicCount)
                {
                    DrawCenteredText(g, "Selected harmonic is not available in model", width, height, dimTextBrush, sectionFont);
                    return bitmap;
                }

                int featureIndex = classIndex * _model.HarmonicCount + harmonicIndex;
                if (_model.RessWeights == null ||
                    featureIndex < 0 ||
                    featureIndex >= _model.RessWeights.Length ||
                    _model.RessWeights[featureIndex] == null)
                {
                    DrawCenteredText(g, "RESS weights are not available", width, height, dimTextBrush, sectionFont);
                    return bitmap;
                }

                double frequency = _model.GetFeatureFrequency(featureIndex);
                string summary =
                    "C" + _model.ClassIds[classIndex].ToString() +
                    "   ·   " + frequency.ToString("0.##") + " Hz";

                SizeF summarySize = g.MeasureString(summary, smallFont);
                g.DrawString(
                    summary,
                    smallFont,
                    dimTextBrush,
                    Math.Max(margin + 160.0f, width - margin - summarySize.Width),
                    12.0f);

                DrawColorScale(
                    g,
                    new RectangleF(margin, contentTop, Math.Max(1.0f, width - 2.0f * margin), 24.0f),
                    smallFont,
                    dimTextBrush);

                List<int> visibleChannels = GetVisibleChannelIndices();
                if (visibleChannels.Count == 0)
                {
                    DrawCenteredText(g, "No scalp EEG channels in model", width, height, dimTextBrush, sectionFont);
                    return bitmap;
                }

                RectangleF contentRect = new RectangleF(
                    margin,
                    contentTop + 34.0f,
                    Math.Max(1.0f, width - 2.0f * margin),
                    Math.Max(1.0f, height - contentTop - 34.0f - margin));

                if (_viewMode == RessSpatialViewMode.Topography)
                {
                    DrawTopography(
                        g,
                        contentRect,
                        frequency,
                        _model.RessWeights[featureIndex],
                        visibleChannels,
                        sectionFont,
                        mapFont,
                        textBrush,
                        dimTextBrush);
                }
                else
                {
                    DrawWeightGrid(
                        g,
                        contentRect,
                        frequency,
                        _model.RessWeights[featureIndex],
                        visibleChannels,
                        sectionFont,
                        channelFont,
                        valueFont,
                        textBrush,
                        cellBorderPen);
                }
            }

            return bitmap;
        }

        private void DrawSpectrumGrid(Graphics g, int width, int height,
            Font channelFont, Font smallFont, Brush textBrush, Brush dimBrush, Pen border)
        {
            g.DrawString(_spectrumStatus ?? "", smallFont, dimBrush, 16, 10);
            int channels = _spectrumChannelNames.Length;
            if (_spectra == null || channels == 0)
                return;
            bool zoomed = _zoomedSpectrumChannel >= 0 && _zoomedSpectrumChannel < channels;
            int visibleCount = zoomed ? 1 : channels;
            for (int item = 0; item < visibleCount; item++)
            {
                int ch = zoomed ? _zoomedSpectrumChannel : item;
                RectangleF cell = GetSpectrumCellBounds(width, height, item, visibleCount);
                // Same orientation, logarithmic frequency rows, palette and brightness
                // as the time-frequency viewer. A static spectrum is one wide column.
                Color color = GlScope.GetChannelColor(ch);
                float plotTop = Math.Min(cell.Bottom, cell.Top + 22);
                float footerHeight = zoomed && !string.IsNullOrEmpty(_spectrumWindowText) ? smallFont.Height + 8 : 0;
                float plotHeight = Math.Max(0, cell.Bottom - plotTop - footerHeight);
                float axisWidth = zoomed ? Math.Min(42.0f, cell.Width * 0.25f) : 0.0f;
                RectangleF plot = new RectangleF(cell.Left + axisWidth, plotTop,
                    Math.Max(0, cell.Width - axisWidth), plotHeight);
                DrawSpectrumColumn(g, plot, ch, color);
                if (zoomed && plot.Height > 0)
                    DrawSpectrumFrequencyGuides(g, cell.Left, plot, smallFont, dimBrush);
                if (footerHeight > 0)
                {
                    using (var footerFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(_spectrumWindowText, smallFont, dimBrush,
                            new RectangleF(cell.Left, cell.Bottom - footerHeight, cell.Width, footerHeight), footerFormat);
                }
                g.DrawRectangle(border, cell.X, cell.Y, cell.Width, cell.Height);
                using (Brush nameBrush = new SolidBrush(color))
                using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(_spectrumChannelNames[ch], channelFont, nameBrush,
                        new RectangleF(cell.Left + 3, cell.Top + 2, Math.Max(1, cell.Width - 6), 20), format);
            }
        }

        private void DrawSpectrumColumn(Graphics g, RectangleF plot, int channel, Color color)
        {
            if (plot.Width <= 0 || plot.Height <= 0)
                return;
            int bins = TimeFrequencySpectrum.FrequencyRows;
            using (Bitmap column = new Bitmap(1, bins, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (ImageAttributes attributes = new ImageAttributes())
            {
                for (int bin = 0; bin < bins; bin++)
                {
                    float intensity = TimeFrequencySpectrum.GetIntensity(
                        _spectra[channel * bins + bin], _spectrumScaleVolts);
                    column.SetPixel(0, bins - bin - 1, Color.FromArgb(
                        (int)(color.R * intensity), (int)(color.G * intensity), (int)(color.B * intensity)));
                }
                // Like GL's linear texture filtering in TimeFreq: interpolate displayed
                // colors between frequency rows, leaving calculated amplitudes untouched.
                attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                var state = g.Save();
                try
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(column, new[] { new PointF(plot.Left, plot.Top),
                        new PointF(plot.Right, plot.Top), new PointF(plot.Left, plot.Bottom) },
                        new RectangleF(0, 0, 1, bins), GraphicsUnit.Pixel, attributes);
                }
                finally { g.Restore(state); }
            }
        }

        private void DrawSpectrumFrequencyGuides(Graphics g, float axisLeft, RectangleF plot,
            Font font, Brush brush)
        {
            double min = TimeFrequencySpectrum.MinFrequency;
            double max = _spectrumMaxFrequency;
            double logRange = Math.Log(max / min);
            if (logRange <= 0)
                return;
            double[] ticks = { 0.5, 1, 2, 5, 10, 20, 50 };
            using (Pen pen = new Pen(Color.FromArgb(100, 110, 110, 110), 1.0f))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                for (int i = 0; i <= ticks.Length; i++)
                {
                    double frequency = i < ticks.Length ? ticks[i] : max;
                    if (frequency > max || (i == ticks.Length && Array.IndexOf(ticks, max) >= 0))
                        continue;
                    float y = plot.Bottom - (float)(Math.Log(frequency / min) / logRange) * plot.Height;
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                    float labelY = Math.Max(plot.Top + font.Height * 0.5f,
                        Math.Min(plot.Bottom - font.Height * 0.5f, y));
                    g.DrawString(frequency.ToString("0.#") + " Hz", font, brush,
                        new RectangleF(axisLeft, labelY - font.Height * 0.5f,
                            Math.Max(1, plot.Left - axisLeft - 5), font.Height), format);
                }
            }
        }

        private void DrawWeightGrid(
            Graphics g,
            RectangleF rect,
            double frequency,
            double[] weights,
            List<int> visibleChannels,
            Font sectionFont,
            Font channelFont,
            Font valueFont,
            Brush textBrush,
            Pen cellBorderPen)
        {
            string title = frequency.ToString("0.##") + " Hz";
            g.DrawString(title, sectionFont, textBrush, rect.Left, rect.Top);

            double maxAbs = GetMaxAbsWeight(weights, visibleChannels);

            int columns = visibleChannels.Count <= 7
                ? visibleChannels.Count
                : 7;
            int rows = (visibleChannels.Count + columns - 1) / columns;

            const float horizontalGap = 5.0f;
            const float verticalGap = 5.0f;
            float gridTop = rect.Top + 24.0f;
            float gridHeight = Math.Max(1.0f, rect.Bottom - gridTop);
            float cellWidth = Math.Max(
                1.0f,
                (rect.Width - horizontalGap * (columns - 1)) / columns);
            float cellHeight = Math.Max(
                1.0f,
                (gridHeight - verticalGap * (rows - 1)) / rows);

            for (int itemIndex = 0; itemIndex < visibleChannels.Count; itemIndex++)
            {
                int channelIndex = visibleChannels[itemIndex];
                int row = itemIndex / columns;
                int column = itemIndex % columns;

                float left = rect.Left + column * (cellWidth + horizontalGap);
                float top = gridTop + row * (cellHeight + verticalGap);
                RectangleF cellRect = new RectangleF(left, top, cellWidth, cellHeight);

                double value = weights[channelIndex];
                double normalized = value / maxAbs;
                Color fillColor = GetWeightColor(normalized);

                using (Brush fillBrush = new SolidBrush(fillColor))
                    g.FillRectangle(fillBrush, cellRect);

                g.DrawRectangle(cellBorderPen, cellRect.X, cellRect.Y, cellRect.Width, cellRect.Height);

                Color foreground = GetReadableTextColor(fillColor);
                using (Brush foregroundBrush = new SolidBrush(foreground))
                {
                    string channelName = _model.ChannelNames[channelIndex];
                    string valueText = value.ToString("+0.000;-0.000;0.000");

                    SizeF channelSize = g.MeasureString(channelName, channelFont);
                    SizeF valueSize = g.MeasureString(valueText, valueFont);

                    float textBlockHeight = channelSize.Height + valueSize.Height + 2.0f;
                    float startY = cellRect.Top + (cellRect.Height - textBlockHeight) * 0.5f;

                    g.DrawString(
                        channelName,
                        channelFont,
                        foregroundBrush,
                        cellRect.Left + (cellRect.Width - channelSize.Width) * 0.5f,
                        startY);

                    g.DrawString(
                        valueText,
                        valueFont,
                        foregroundBrush,
                        cellRect.Left + (cellRect.Width - valueSize.Width) * 0.5f,
                        startY + channelSize.Height + 2.0f);
                }
            }
        }

        private void DrawTopography(
            Graphics g,
            RectangleF rect,
            double frequency,
            double[] weights,
            List<int> visibleChannels,
            Font sectionFont,
            Font mapFont,
            Brush textBrush,
            Brush dimTextBrush)
        {
            string title = frequency.ToString("0.##") + " Hz";
            g.DrawString(title, sectionFont, textBrush, rect.Left, rect.Top);

            float mapTop = rect.Top + 22.0f;
            float availableHeight = Math.Max(1.0f, rect.Bottom - mapTop);
            float mapSize = Math.Min(rect.Width, availableHeight);
            RectangleF mapRect = new RectangleF(
                rect.Left + (rect.Width - mapSize) * 0.5f,
                mapTop + (availableHeight - mapSize) * 0.5f,
                mapSize,
                mapSize);

            using (Pen outlinePen = new Pen(Color.FromArgb(115, 135, 155), 1.2f))
            using (Pen guidePen = new Pen(Color.FromArgb(50, 65, 80), 1.0f))
            using (Pen electrodePen = new Pen(Color.FromArgb(85, 85, 85), 1.0f))
            {
                DrawHeadOutline(g, mapRect, outlinePen, guidePen);

                Dictionary<string, int> channelIndexByName = BuildTopographyChannelIndex(visibleChannels);
                double maxAbs = GetMaxAbsWeight(weights, visibleChannels);
                int mappedCount = 0;

                float nodeDiameter = Math.Max(20.0f, Math.Min(36.0f, mapSize * 0.054f));

                for (int i = 0; i < TopographyElectrodes.Length; i++)
                {
                    TopographyElectrode electrode = TopographyElectrodes[i];
                    string key = NormalizeChannelName(electrode.Name);

                    int channelIndex;
                    bool active = channelIndexByName.TryGetValue(key, out channelIndex);
                    Color fillColor = Color.White;

                    if (active)
                    {
                        double normalized = weights[channelIndex] / maxAbs;
                        fillColor = GetWeightColor(normalized);
                        mappedCount++;
                    }

                    float centerX = mapRect.Left + electrode.X * mapRect.Width;
                    float centerY = mapRect.Top + electrode.Y * mapRect.Height;
                    RectangleF nodeRect = new RectangleF(
                        centerX - nodeDiameter * 0.5f,
                        centerY - nodeDiameter * 0.5f,
                        nodeDiameter,
                        nodeDiameter);

                    using (Brush fillBrush = new SolidBrush(fillColor))
                        g.FillEllipse(fillBrush, nodeRect);

                    g.DrawEllipse(electrodePen, nodeRect);

                    Color foreground = GetReadableTextColor(fillColor);
                    using (Brush foregroundBrush = new SolidBrush(foreground))
                    {
                        SizeF labelSize = g.MeasureString(electrode.Name, mapFont);
                        g.DrawString(
                            electrode.Name,
                            mapFont,
                            foregroundBrush,
                            centerX - labelSize.Width * 0.5f,
                            centerY - labelSize.Height * 0.5f);
                    }
                }

                if (mappedCount == 0)
                {
                    string message = "No 10-10 channel names found in model";
                    SizeF size = g.MeasureString(message, mapFont);
                    g.DrawString(
                        message,
                        mapFont,
                        dimTextBrush,
                        rect.Left + (rect.Width - size.Width) * 0.5f,
                        rect.Bottom - size.Height - 2.0f);
                }
            }
        }

        private static void DrawHeadOutline(
            Graphics g,
            RectangleF mapRect,
            Pen outlinePen,
            Pen guidePen)
        {
            RectangleF headRect = MapSourceRectangle(mapRect, 60.0f, 90.0f, 896.0f, 900.0f);
            g.DrawEllipse(outlinePen, headRect);

            PointF noseTop = MapSourcePoint(mapRect, 507.5f, 20.0f);
            PointF noseLeft = MapSourcePoint(mapRect, 464.0f, 90.0f);
            PointF noseRight = MapSourcePoint(mapRect, 552.0f, 90.0f);
            g.DrawLine(outlinePen, noseLeft, noseTop);
            g.DrawLine(outlinePen, noseTop, noseRight);

            PointF verticalTop = MapSourcePoint(mapRect, 507.5f, 90.0f);
            PointF verticalBottom = MapSourcePoint(mapRect, 507.5f, 990.0f);
            g.DrawLine(guidePen, verticalTop, verticalBottom);

            PointF horizontalLeft = MapSourcePoint(mapRect, 60.0f, 540.0f);
            PointF horizontalRight = MapSourcePoint(mapRect, 956.0f, 540.0f);
            g.DrawLine(guidePen, horizontalLeft, horizontalRight);
        }

        private static PointF MapSourcePoint(RectangleF mapRect, float sourceX, float sourceY)
        {
            return new PointF(
                mapRect.Left + (sourceX / 1023.0f) * mapRect.Width,
                mapRect.Top + (sourceY / 1026.0f) * mapRect.Height);
        }

        private static RectangleF MapSourceRectangle(
            RectangleF mapRect,
            float sourceX,
            float sourceY,
            float sourceWidth,
            float sourceHeight)
        {
            PointF topLeft = MapSourcePoint(mapRect, sourceX, sourceY);
            return new RectangleF(
                topLeft.X,
                topLeft.Y,
                sourceWidth / 1023.0f * mapRect.Width,
                sourceHeight / 1026.0f * mapRect.Height);
        }

        private Dictionary<string, int> BuildTopographyChannelIndex(List<int> visibleChannels)
        {
            Dictionary<string, int> result =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            HashSet<string> knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < TopographyElectrodes.Length; i++)
                knownNames.Add(NormalizeChannelName(TopographyElectrodes[i].Name));

            for (int i = 0; i < visibleChannels.Count; i++)
            {
                int channelIndex = visibleChannels[i];
                string key = NormalizeChannelName(_model.ChannelNames[channelIndex]);

                if (!knownNames.Contains(key))
                    continue;

                if (!result.ContainsKey(key))
                    result.Add(key, channelIndex);
            }

            return result;
        }

        private void DrawColorScale(
            Graphics g,
            RectangleF rect,
            Font font,
            Brush dimTextBrush)
        {
            const int steps = 128;
            float scaleWidth = Math.Min(260.0f, Math.Max(120.0f, rect.Width * 0.42f));
            float scaleHeight = 7.0f;
            float scaleLeft = rect.Left;
            float scaleTop = rect.Top + 1.0f;

            for (int i = 0; i < steps; i++)
            {
                double normalized = -1.0 + 2.0 * i / (steps - 1.0);
                float x0 = scaleLeft + scaleWidth * i / steps;
                float x1 = scaleLeft + scaleWidth * (i + 1) / steps;

                using (Brush brush = new SolidBrush(GetWeightColor(normalized)))
                    g.FillRectangle(brush, x0, scaleTop, Math.Max(1.0f, x1 - x0 + 0.5f), scaleHeight);
            }

            g.DrawString("negative", font, dimTextBrush, scaleLeft, scaleTop + 9.0f);

            string zeroText = "0";
            SizeF zeroSize = g.MeasureString(zeroText, font);
            g.DrawString(
                zeroText,
                font,
                dimTextBrush,
                scaleLeft + scaleWidth * 0.5f - zeroSize.Width * 0.5f,
                scaleTop + 9.0f);

            string positiveText = "positive";
            SizeF positiveSize = g.MeasureString(positiveText, font);
            g.DrawString(
                positiveText,
                font,
                dimTextBrush,
                scaleLeft + scaleWidth - positiveSize.Width,
                scaleTop + 9.0f);
        }

        private List<int> GetVisibleChannelIndices()
        {
            List<int> result = new List<int>();

            if (_model == null || _model.ChannelNames == null)
                return result;

            for (int i = 0; i < _model.ChannelNames.Length; i++)
            {
                if (IsScalpEegChannelName(_model.ChannelNames[i]))
                    result.Add(i);
            }

            return result;
        }

        private static bool IsScalpEegChannelName(string channelName)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                return false;

            string normalized = NormalizeChannelName(channelName);

            if (normalized == "A1" || normalized == "A2" || normalized == "GND")
                return false;

            if (normalized.StartsWith("VEOG", StringComparison.Ordinal) ||
                normalized.StartsWith("HEOG", StringComparison.Ordinal) ||
                normalized.StartsWith("EOG", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static string NormalizeChannelName(string channelName)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                return string.Empty;

            return channelName
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty);
        }

        private int GetClassIndexById(int? classId)
        {
            if (_model == null || !classId.HasValue)
                return -1;

            for (int i = 0; i < _model.ClassCount; i++)
            {
                if (_model.ClassIds[i] == classId.Value)
                    return i;
            }

            return -1;
        }

        private static double GetMaxAbsWeight(double[] weights, List<int> visibleChannels)
        {
            double maxAbs = 0.0;

            for (int i = 0; i < visibleChannels.Count; i++)
            {
                int channelIndex = visibleChannels[i];
                if (channelIndex < 0 || channelIndex >= weights.Length)
                    continue;

                double value = Math.Abs(weights[channelIndex]);
                if (value > maxAbs)
                    maxAbs = value;
            }

            return maxAbs > 1e-15 ? maxAbs : 1.0;
        }

        private static Color GetWeightColor(double normalized)
        {
            if (normalized < -1.0)
                normalized = -1.0;
            if (normalized > 1.0)
                normalized = 1.0;

            Color negative = Color.FromArgb(0, 0, 255);
            Color neutral = Color.White;
            Color positive = Color.FromArgb(255, 0, 0);

            if (normalized < 0.0)
                return InterpolateColor(negative, neutral, normalized + 1.0);

            return InterpolateColor(neutral, positive, normalized);
        }

        private static Color InterpolateColor(Color from, Color to, double t)
        {
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;

            int r = (int)Math.Round(from.R + (to.R - from.R) * t);
            int g = (int)Math.Round(from.G + (to.G - from.G) * t);
            int b = (int)Math.Round(from.B + (to.B - from.B) * t);

            return Color.FromArgb(r, g, b);
        }

        private static Color GetReadableTextColor(Color background)
        {
            double luminance =
                0.2126 * background.R +
                0.7152 * background.G +
                0.0722 * background.B;

            return luminance < 145.0
                ? Color.White
                : Color.Black;
        }

        private static void DrawCenteredText(
            Graphics g,
            string text,
            int width,
            int height,
            Brush brush,
            Font font)
        {
            SizeF size = g.MeasureString(text, font);
            g.DrawString(
                text,
                font,
                brush,
                Math.Max(0.0f, (width - size.Width) * 0.5f),
                Math.Max(0.0f, (height - size.Height) * 0.5f));
        }

        public void Dispose()
        {
            _glCtrl.Paint -= GlCtrl_Paint;
            _glCtrl.Resize -= GlCtrl_Resize;
            _glCtrl.MouseDoubleClick -= GlCtrl_MouseDoubleClick;

            if (_textureId != 0 && _glInitialized && !_glCtrl.IsDisposed && _glCtrl.IsHandleCreated)
            {
                try
                {
                    _glCtrl.MakeCurrent();
                    GL.DeleteTexture(_textureId);
                }
                catch
                {
                }

                _textureId = 0;
            }

            if (_bitmap != null)
            {
                _bitmap.Dispose();
                _bitmap = null;
            }
        }
    }
}
