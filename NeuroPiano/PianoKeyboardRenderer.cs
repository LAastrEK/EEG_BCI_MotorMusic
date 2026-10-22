using System.Drawing;
using OpenTK.Graphics.OpenGL;

namespace NeuroPiano
{
    public sealed class PianoKeyboardRenderer
    {
        public void SetupViewport(int width, int height)
        {
            if (width < 1)
                width = 1;

            if (height < 1)
                height = 1;

            GL.Viewport(0, 0, width, height);

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0.0, width, height, 0.0, -1.0, 1.0);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.CullFace);
            GL.ClearColor(0.050f, 0.050f, 0.055f, 1.0f);
        }

        public void Render(PianoKeyboardLayout layout, int pressedMidiNumber, SSVEPStimulusEngine ssvepStimulusEngine, int activeStimulusVisibleIndex,
            int attentionCueVisibleIndex, int feedbackVisibleIndex, int backgroundCueVisibleIndex)
        {
            GL.Clear(ClearBufferMask.ColorBufferBit);

            if (layout == null)
                return;

            for (int i = 0; i < layout.WhiteKeys.Count; i++)
            {
                PianoKey key = layout.WhiteKeys[i];

                DrawWhiteKey(
                    key,
                    i,
                    key.MidiNumber == pressedMidiNumber,
                    ssvepStimulusEngine,
                    activeStimulusVisibleIndex,
                    feedbackVisibleIndex);
            }

            for (int i = 0; i < layout.BlackKeys.Count; i++)
            {
                PianoKey key = layout.BlackKeys[i];
                DrawBlackKey(key, key.MidiNumber == pressedMidiNumber);
            }

            if (attentionCueVisibleIndex >= 0 &&
                attentionCueVisibleIndex < layout.WhiteKeys.Count)
            {
                DrawTargetCueBorder(
                    layout.WhiteKeys[attentionCueVisibleIndex].Bounds);
            }

            DrawBorder(layout.Bounds, 2.0f, 0.10f, 0.10f, 0.10f);

            for (int i = 0; i < layout.BackgroundTargets.Count; i++)
                DrawBorder(layout.BackgroundTargets[i], 1.0f, 1.0f, 1.0f, 1.0f);

            if (backgroundCueVisibleIndex >= 0 &&
                backgroundCueVisibleIndex < layout.BackgroundTargets.Count)
            {
                DrawTargetCueBorder(layout.BackgroundTargets[backgroundCueVisibleIndex]);
            }
        }

        private static void DrawWhiteKey(PianoKey key, int visibleIndex, bool pressed, SSVEPStimulusEngine ssvepStimulusEngine, 
            int activeStimulusVisibleIndex, int feedbackVisibleIndex)
        {
            RectangleF r = key.Bounds;

            if (pressed || visibleIndex == feedbackVisibleIndex)
            {
                GL.Color4(0.72f, 0.72f, 0.70f, 1.0f);
            }
            else
            {
                double brightness = 1.0;

                bool stimulusEnabled =
                    activeStimulusVisibleIndex == -1 ||
                    activeStimulusVisibleIndex == visibleIndex;

                if (stimulusEnabled &&
                    ssvepStimulusEngine != null)
                {
                    brightness = ssvepStimulusEngine.GetBrightness(visibleIndex);
                }

                float b = (float)brightness;

                GL.Color4(b, b, b, 1.0f);
            }

            FillRect(r);
            DrawBorder(r, 1.25f, 0.12f, 0.12f, 0.12f);
        }

        private static void DrawBlackKey(PianoKey key, bool pressed)
        {
            RectangleF r = key.Bounds;

            if (pressed)
                GL.Color4(0.28f, 0.28f, 0.29f, 1.0f);
            else
                GL.Color4(0.035f, 0.035f, 0.040f, 1.0f);

            FillRect(r);
            DrawBorder(r, 1.25f, 0.42f, 0.42f, 0.43f);
        }

        private static void DrawTargetCueBorder(RectangleF bounds)
        {
            const float inset = 3.0f;

            RectangleF r = new RectangleF(
                bounds.Left + inset,
                bounds.Top + inset,
                bounds.Width - inset * 2.0f,
                bounds.Height - inset * 2.0f);

            DrawBorder(r, 4.0f, 0.95f, 0.20f, 0.10f);
        }

        private static void FillRect(RectangleF r)
        {
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(r.Left, r.Top);
            GL.Vertex2(r.Right, r.Top);
            GL.Vertex2(r.Right, r.Bottom);
            GL.Vertex2(r.Left, r.Bottom);
            GL.End();
        }

        private static void DrawBorder(RectangleF r, float width, float red, float green, float blue)
        {
            GL.LineWidth(width);
            GL.Color4(red, green, blue, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex2(r.Left, r.Top);
            GL.Vertex2(r.Right, r.Top);
            GL.Vertex2(r.Right, r.Bottom);
            GL.Vertex2(r.Left, r.Bottom);
            GL.End();
        }
    }
}
