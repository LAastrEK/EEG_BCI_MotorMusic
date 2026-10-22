using OpenTK;
using OpenTK.Graphics.OpenGL;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PolyHubNet
{
    public sealed class AnalysisGlView : IDisposable
    {
        private readonly GLControl _glCtrl;

        private SsvepModel _model;
        private SsvepAnalysisResult _result;
        private int? _selectedClassId;
        private string _statusText;

        private bool _glInitialized;
        private bool _textureDirty = true;
        private int _textureId;
        private Bitmap _bitmap;

        public AnalysisGlView(GLControl glCtrl)
        {
            if (glCtrl == null)
                throw new ArgumentNullException("glCtrl");

            _glCtrl = glCtrl;
            _glCtrl.Paint += GlCtrl_Paint;
            _glCtrl.Resize += GlCtrl_Resize;

            _statusText = "SSVEP model not loaded";
        }

        public void SetModel(SsvepModel model)
        {
            _model = model;
            _result = null;
            _statusText = model != null
                ? "Model loaded. Waiting for EEG..."
                : "SSVEP model not loaded";

            RequestRedraw();
        }

        public void SetResult(SsvepAnalysisResult result)
        {
            _result = result;
            if (result != null)
                _statusText = null;

            RequestRedraw();
        }

        public void SetSelectedClassId(int? classId)
        {
            _selectedClassId = classId;
            RequestRedraw();
        }

        public void SetStatus(string text)
        {
            _result = null;
            _selectedClassId = null;
            _statusText = text;
            RequestRedraw();
        }

        public void Clear()
        {
            _result = null;
            _statusText = _model != null
                ? "Model loaded. Waiting for EEG..."
                : "SSVEP model not loaded";

            RequestRedraw();
        }

        private void RequestRedraw()
        {
            _textureDirty = true;

            if (!_glCtrl.IsDisposed && _glCtrl.IsHandleCreated)
                _glCtrl.Invalidate();
        }

        private void GlCtrl_Resize(object sender, EventArgs e)
        {
            _textureDirty = true;
            if (!_glCtrl.IsDisposed && _glCtrl.IsHandleCreated)
                _glCtrl.Invalidate();
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
            using (Font labelFont = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (Font smallFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (Pen framePen = new Pen(Color.FromArgb(110, 110, 110)))
            using (Pen selectedPen = new Pen(Color.White, 2.0f))
            using (Pen learnedPen = new Pen(Color.White, 1.0f))
            using (Brush textBrush = new SolidBrush(Color.White))
            using (Brush dimTextBrush = new SolidBrush(Color.FromArgb(180, 180, 180)))
            using (Brush barBrush = new SolidBrush(Color.FromArgb(155, 155, 155)))
            using (Brush featureBrush = new SolidBrush(Color.FromArgb(125, 125, 125)))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                const float margin = 16.0f;

                g.DrawString("SSVEP analysis", titleFont, textBrush, margin, 10.0f);

                if (_result != null && (_result.IsRejected || _result.Features.Length == 0))
                {
                    g.DrawString(_result.IsRejected ? "Rejected: " + _result.EogRejection.Reason
                        : "EOG: clean. Waiting for a compatible SSVEP model/input.", labelFont,
                        dimTextBrush, new RectangleF(margin, 44.0f, width - 2 * margin, height - 44.0f));
                    return bitmap;
                }
                if (_model == null)
                {
                    DrawCenteredStatus(g, width, height, margin, labelFont, dimTextBrush);
                    return bitmap;
                }

                int selectedClassIndex = GetClassIndexById(_selectedClassId);

                if (_result != null)
                {
                    int predictedClassIndex = GetClassIndexById(_result.PredictedClassId);
                    if (predictedClassIndex >= 0)
                    {
                        string summaryText =
                            "Accepted C" + _model.ClassIds[predictedClassIndex].ToString() +
                            " (" + _model.TargetFrequencies[predictedClassIndex].ToString("0.##") + " Hz)";

                        if (selectedClassIndex >= 0)
                        {
                            summaryText +=
                                "   |   selected C" + _model.ClassIds[selectedClassIndex].ToString() +
                                " (" + _model.TargetFrequencies[selectedClassIndex].ToString("0.##") + " Hz)";
                        }

                        g.DrawString(summaryText, smallFont, dimTextBrush, 158.0f, 12.0f);
                    }
                    else
                    {
                        string text = _model.HasClassAcceptance && _model.BackgroundFeatureCenters.Length > 0
                            ? "None / неопределённо" : "None / нет фонового эталона (1200)";
                        g.DrawString(text, smallFont, dimTextBrush, 158.0f, 12.0f);
                    }
                }
                else if (selectedClassIndex >= 0)
                {
                    string summaryText =
                        "Model class C" + _model.ClassIds[selectedClassIndex].ToString() +
                        " (" + _model.TargetFrequencies[selectedClassIndex].ToString("0.##") + " Hz)";
                    g.DrawString(summaryText, smallFont, dimTextBrush, 132.0f, 12.0f);
                }

                if (_result == null)
                {
                    if (selectedClassIndex >= 0 && _model.ClassFeatureMeans != null)
                    {
                        DrawSelectedClassHeader(
                            g,
                            new RectangleF(margin, 42.0f, Math.Max(1.0f, width - 2.0f * margin), 70.0f),
                            selectedClassIndex,
                            labelFont,
                            smallFont,
                            framePen,
                            selectedPen,
                            textBrush,
                            dimTextBrush);

                        DrawFeatureVector(
                            g,
                            new RectangleF(margin, 120.0f, Math.Max(1.0f, width - 2.0f * margin), Math.Max(90.0f, height - 136.0f)),
                            _model.ClassFeatureMeans[selectedClassIndex],
                            selectedClassIndex,
                            "Learned harmonic fingerprint",
                            labelFont,
                            smallFont,
                            framePen,
                            learnedPen,
                            textBrush,
                            dimTextBrush,
                            featureBrush);
                    }
                    else
                    {
                        DrawCenteredStatus(g, width, height, margin, labelFont, dimTextBrush);
                    }

                    return bitmap;
                }

                DrawClassScores(
                    g,
                    new RectangleF(margin, 38.0f, Math.Max(1.0f, width - 2.0f * margin), Math.Max(120.0f, height * 0.46f)),
                    selectedClassIndex,
                    labelFont,
                    smallFont,
                    framePen,
                    selectedPen,
                    textBrush,
                    dimTextBrush,
                    barBrush);

                float featuresTop = Math.Max(190.0f, height * 0.57f);
                int referenceClassIndex = selectedClassIndex >= 0
                    ? selectedClassIndex
                    : GetClassIndexById(_result.PredictedClassId);

                DrawFeatureVector(
                    g,
                    new RectangleF(margin, featuresTop, Math.Max(1.0f, width - 2.0f * margin), Math.Max(70.0f, height - featuresTop - margin)),
                    _result.Features,
                    referenceClassIndex,
                    selectedClassIndex >= 0
                        ? "Current RESS features / selected class fingerprint"
                        : "Current RESS harmonic features",
                    labelFont,
                    smallFont,
                    framePen,
                    learnedPen,
                    textBrush,
                    dimTextBrush,
                    featureBrush);
            }

            return bitmap;
        }

        private void DrawClassScores(
            Graphics g,
            RectangleF rect,
            int selectedClassIndex,
            Font labelFont,
            Font smallFont,
            Pen framePen,
            Pen selectedPen,
            Brush textBrush,
            Brush dimTextBrush,
            Brush barBrush)
        {
            int classCount = _model.ClassCount;
            double[] margins = _result.ClassAcceptanceMargins;

            g.DrawString("Class acceptance margin (0 = threshold, scale ±2)", labelFont, textBrush, rect.Left, rect.Top);

            float plotTop = rect.Top + 24.0f;
            float plotBottom = rect.Bottom - 40.0f;
            float plotHeight = Math.Max(10.0f, plotBottom - plotTop);
            float slotWidth = rect.Width / classCount;
            float zeroY = (plotTop + plotBottom) * 0.5f;
            g.DrawLine(framePen, rect.Left, zeroY, rect.Right, zeroY);

            for (int k = 0; k < classCount; k++)
            {
                float slotLeft = rect.Left + k * slotWidth;
                float barWidth = Math.Max(4.0f, slotWidth * 0.54f);
                float barLeft = slotLeft + (slotWidth - barWidth) * 0.5f;
                double margin = margins[k];
                float valueY = SsvepAcceptance.IsFinite(margin)
                    ? zeroY - (float)(Math.Max(-2.0, Math.Min(2.0, margin)) / 2.0 * plotHeight * 0.5f) : zeroY;
                float barTop = Math.Min(zeroY, valueY);
                float barHeight = Math.Abs(valueY - zeroY);

                RectangleF barRect = new RectangleF(barLeft, barTop, barWidth, barHeight);
                g.FillRectangle(margin > 0.0 ? barBrush : dimTextBrush, barRect);
                g.DrawRectangle(framePen, barLeft, plotTop, barWidth, plotHeight);

                bool predicted = _model.ClassIds[k] == _result.PredictedClassId;
                if (predicted)
                    g.DrawRectangle(selectedPen, barLeft - 2.0f, plotTop - 2.0f, barWidth + 4.0f, plotHeight + 4.0f);

                if (selectedClassIndex == k && !predicted)
                {
                    using (Pen expectedPen = new Pen(Color.FromArgb(190, 190, 190), 1.5f))
                    {
                        expectedPen.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(expectedPen, barLeft - 2.0f, plotTop - 2.0f, barWidth + 4.0f, plotHeight + 4.0f);
                    }
                }

                string marginText = SsvepAcceptance.IsFinite(margin)
                    ? (margin > 0.0 ? "+" : "") + margin.ToString("G3") : "—";
                SizeF marginSize = g.MeasureString(marginText, smallFont);
                g.DrawString(
                    marginText,
                    smallFont,
                    predicted ? textBrush : dimTextBrush,
                    slotLeft + (slotWidth - marginSize.Width) * 0.5f,
                    Math.Max(plotTop, barTop - marginSize.Height - 2.0f));

                string classText = "C" + _model.ClassIds[k].ToString();
                string frequencyText = _model.TargetFrequencies[k].ToString("0.##") + " Hz";

                SizeF classSize = g.MeasureString(classText, labelFont);
                SizeF frequencySize = g.MeasureString(frequencyText, smallFont);

                g.DrawString(
                    classText,
                    labelFont,
                    predicted ? textBrush : dimTextBrush,
                    slotLeft + (slotWidth - classSize.Width) * 0.5f,
                    plotBottom + 5.0f);

                g.DrawString(
                    frequencyText,
                    smallFont,
                    dimTextBrush,
                    slotLeft + (slotWidth - frequencySize.Width) * 0.5f,
                    plotBottom + 21.0f);
            }
        }

        private void DrawFeatureVector(
            Graphics g,
            RectangleF rect,
            double[] featureValues,
            int referenceClassIndex,
            string title,
            Font labelFont,
            Font smallFont,
            Pen framePen,
            Pen learnedPen,
            Brush textBrush,
            Brush dimTextBrush,
            Brush featureBrush)
        {
            int featureCount = _model.FeatureCount;
            int harmonicCount = _model.HarmonicCount;

            g.DrawString(title, labelFont, textBrush, rect.Left, rect.Top);

            float plotTop = rect.Top + 24.0f;
            float plotBottom = rect.Bottom - 28.0f;
            float plotHeight = Math.Max(10.0f, plotBottom - plotTop);
            float slotWidth = rect.Width / featureCount;

            for (int feature = 0; feature < featureCount; feature++)
            {
                if (!SsvepAcceptance.IsFinite(featureValues[feature])) continue;
                double min = double.PositiveInfinity;
                double max = double.NegativeInfinity;

                if (_model.ClassFeatureMeans != null)
                {
                    for (int k = 0; k < _model.ClassFeatureMeans.Length; k++)
                    {
                        double value = _model.ClassFeatureMeans[k][feature];
                        if (value < min) min = value;
                        if (value > max) max = value;
                    }
                }

                if (double.IsInfinity(min) || double.IsInfinity(max) || max - min < 1e-9)
                {
                    min = featureValues[feature] - 1.0;
                    max = featureValues[feature] + 1.0;
                }

                double currentNormalized = (featureValues[feature] - min) / (max - min);
                currentNormalized = Clamp01(currentNormalized);

                float slotLeft = rect.Left + feature * slotWidth;
                float barWidth = Math.Max(2.0f, slotWidth * 0.55f);
                float barLeft = slotLeft + (slotWidth - barWidth) * 0.5f;
                float barHeight = (float)(currentNormalized * plotHeight);
                float barTop = plotBottom - barHeight;

                g.FillRectangle(featureBrush, barLeft, barTop, barWidth, barHeight);
                g.DrawRectangle(framePen, barLeft, plotTop, barWidth, plotHeight);

                if (_model.ClassFeatureMeans != null &&
                    referenceClassIndex >= 0 &&
                    referenceClassIndex < _model.ClassFeatureMeans.Length)
                {
                    double learned = _model.ClassFeatureMeans[referenceClassIndex][feature];
                    double learnedNormalized = Clamp01((learned - min) / (max - min));
                    float learnedY = plotBottom - (float)(learnedNormalized * plotHeight);
                    g.DrawLine(learnedPen, barLeft - 2.0f, learnedY, barLeft + barWidth + 2.0f, learnedY);
                }

                if (feature % harmonicCount == 0)
                {
                    int classIndex = feature / harmonicCount;
                    string text = _model.TargetFrequencies[classIndex].ToString("0.##");
                    SizeF textSize = g.MeasureString(text, smallFont);
                    float groupWidth = slotWidth * harmonicCount;
                    float groupLeft = rect.Left + classIndex * groupWidth;

                    g.DrawString(
                        text,
                        smallFont,
                        dimTextBrush,
                        groupLeft + (groupWidth - textSize.Width) * 0.5f,
                        plotBottom + 5.0f);
                }
            }
        }

        private void DrawCenteredStatus(
            Graphics g,
            int width,
            int height,
            float margin,
            Font labelFont,
            Brush dimTextBrush)
        {
            string text = !string.IsNullOrWhiteSpace(_statusText)
                ? _statusText
                : "Waiting for EEG...";

            SizeF size = g.MeasureString(text, labelFont);
            g.DrawString(
                text,
                labelFont,
                dimTextBrush,
                Math.Max(margin, (width - size.Width) * 0.5f),
                Math.Max(42.0f, (height - size.Height) * 0.5f));
        }

        private void DrawSelectedClassHeader(
            Graphics g,
            RectangleF rect,
            int selectedClassIndex,
            Font labelFont,
            Font smallFont,
            Pen framePen,
            Pen selectedPen,
            Brush textBrush,
            Brush dimTextBrush)
        {
            int classCount = _model.ClassCount;
            float slotWidth = rect.Width / classCount;

            for (int k = 0; k < classCount; k++)
            {
                float left = rect.Left + k * slotWidth + 3.0f;
                float width = Math.Max(8.0f, slotWidth - 6.0f);
                RectangleF box = new RectangleF(left, rect.Top + 4.0f, width, 50.0f);

                g.DrawRectangle(k == selectedClassIndex ? selectedPen : framePen, box.X, box.Y, box.Width, box.Height);

                string classText = "C" + _model.ClassIds[k].ToString();
                string frequencyText = _model.TargetFrequencies[k].ToString("0.##") + " Hz";

                SizeF classSize = g.MeasureString(classText, labelFont);
                SizeF frequencySize = g.MeasureString(frequencyText, smallFont);

                g.DrawString(
                    classText,
                    labelFont,
                    k == selectedClassIndex ? textBrush : dimTextBrush,
                    box.Left + (box.Width - classSize.Width) * 0.5f,
                    box.Top + 5.0f);

                g.DrawString(
                    frequencyText,
                    smallFont,
                    dimTextBrush,
                    box.Left + (box.Width - frequencySize.Width) * 0.5f,
                    box.Top + 26.0f);
            }
        }

        private int GetClassIndexById(int? classId)
        {
            if (_model == null || !classId.HasValue)
                return -1;

            for (int k = 0; k < _model.ClassCount; k++)
            {
                if (_model.ClassIds[k] == classId.Value)
                    return k;
            }

            return -1;
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        public void Dispose()
        {
            _glCtrl.Paint -= GlCtrl_Paint;
            _glCtrl.Resize -= GlCtrl_Resize;

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
