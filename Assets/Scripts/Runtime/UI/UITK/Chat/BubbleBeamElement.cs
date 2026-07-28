using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>
    /// A light travelling around an assistant bubble's outline while the answer is being streamed
    /// or spoken. Runs at a fixed speed — it marks "this message is active", not how fast.
    ///
    /// Drawn with Painter2D because USS cannot do it: there are no gradients, so the glow is built
    /// from short stroke segments whose alpha falls off symmetrically on both sides of the head.
    /// That symmetry is the whole look — a one-sided comet has a hard leading edge and reads as an
    /// object with a nose, while this reads as a patch of light sliding along the border.
    ///
    /// The path is sampled rather than emitted as exact arcs: segments are ~13px long and sampled
    /// every 2px, which is visually identical at this scale and much harder to get wrong.
    /// </summary>
    public sealed class BubbleBeamElement : VisualElement
    {
        // --- the knobs that decide tasteful vs gaudy ---
        private const float BeamHalfLength = 45f;  // px from the head to where the glow dies out
        private const int TailSegments = 9;        // per side; more = smoother, same total length
        private const float HeadAlpha = 1f;        // alpha at the head, the tail falls off from here
        private const float FalloffPower = 2.4f;   // higher = the light stays tighter around the head
        // -----------------------------------------------

        // Segments are longer than the gap between them, so consecutive ones overlap and their
        // ends blend instead of butting up as visible steps. Their alpha is divided by the same
        // factor, because overlapping strokes compose to roughly the sum of their alphas.
        private const float SegmentOverlap = 2.2f;

        private const float SampleStep = 2f;
        private const float PixelsPerSecond = 190f;
        private const float HeadWidth = 1.5f;
        private const float TailWidth = 1.3f;
        private const float HeadWhiteMix = 0.55f;

        private float _distance;
        private float _lastTime;
        private bool _running;
        private IVisualElementScheduledItem _tick;

        public BubbleBeamElement()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
        }

        public void Play()
        {
            if (_running)
                return;
            _running = true;
            _lastTime = Time.unscaledTime;
            if (_tick == null)
                _tick = schedule.Execute(Step).Every(16);
            else
                _tick.Resume();
        }

        public void Stop()
        {
            if (!_running)
                return;
            _running = false;
            if (_tick != null)
                _tick.Pause();
            // Repaint once more so the last lit frame is cleared instead of freezing on screen.
            MarkDirtyRepaint();
        }

        private void Step()
        {
            float now = Time.unscaledTime;
            float dt = Mathf.Clamp(now - _lastTime, 0f, 0.05f);
            _lastTime = now;

            _distance += PixelsPerSecond * dt;
            MarkDirtyRepaint();
        }

        private void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            if (!_running)
                return;

            Rect box = contentRect;
            if (box.width <= 4f || box.height <= 4f)
                return;

            float radius = ResolveRadius(box);
            float perimeter = Perimeter(box.width, box.height, radius);
            if (perimeter <= 1f)
                return;

            // Wrap here rather than in Step: the bubble grows while text streams in, so the
            // perimeter changes under us and a value clamped at the old length would jump.
            float head = Mathf.Repeat(_distance, perimeter);

            Color baseColor = resolvedStyle.color;
            Color headColor = Color.Lerp(baseColor, Color.white, HeadWhiteMix);

            var painter = ctx.painter2D;
            painter.lineCap = LineCap.Round;

            float step = BeamHalfLength / TailSegments;
            float segmentLength = step * SegmentOverlap;

            // Draw dimmest first so the bright head lands on top where segments overlap.
            for (int ring = TailSegments; ring >= 0; ring--)
            {
                // 0 at the head, 1 at the faded tip — a continuous curve rather than a hand-written
                // ladder, which is what made the steps between segments visible.
                float t = ring / (float)TailSegments;
                float falloff = Mathf.Pow(1f - t, FalloffPower);
                float alpha = HeadAlpha * falloff / SegmentOverlap;

                // The light also thins out towards the tail, which softens the ends further.
                float width = Mathf.Lerp(TailWidth, HeadWidth, falloff);
                Color color = Color.Lerp(baseColor, headColor, falloff);

                if (ring == 0)
                {
                    DrawSegment(painter, box, radius, perimeter, head, segmentLength, color, alpha, width);
                }
                else
                {
                    float offset = ring * step;
                    DrawSegment(painter, box, radius, perimeter, head - offset, segmentLength, color, alpha, width);
                    DrawSegment(painter, box, radius, perimeter, head + offset, segmentLength, color, alpha, width);
                }
            }
        }

        private void DrawSegment(Painter2D painter, Rect box, float radius, float perimeter,
                                 float centre, float length, Color color, float alpha, float width)
        {
            if (alpha <= 0.002f)
                return;

            painter.strokeColor = new Color(color.r, color.g, color.b, color.a * alpha);
            painter.lineWidth = width;
            painter.BeginPath();

            float from = centre - length * 0.5f;
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / SampleStep));
            for (int i = 0; i <= steps; i++)
            {
                float d = from + length * (i / (float)steps);
                Vector2 p = PointAt(box, radius, perimeter, d);
                if (i == 0)
                    painter.MoveTo(p);
                else
                    painter.LineTo(p);
            }

            painter.Stroke();
        }

        /// <summary>
        /// The bubble's own corner radius, minus its border width — this element sits inside the
        /// border, so its corners are that much tighter. Read from the parent every frame instead
        /// of hardcoding 8px, so changing the bubble's radius in USS cannot desync the beam.
        /// </summary>
        private float ResolveRadius(Rect box)
        {
            float radius = 8f;
            float inset = 0f;
            if (parent != null)
            {
                IResolvedStyle rs = parent.resolvedStyle;
                radius = Mathf.Min(
                    Mathf.Min(rs.borderTopLeftRadius, rs.borderTopRightRadius),
                    Mathf.Min(rs.borderBottomLeftRadius, rs.borderBottomRightRadius));
                inset = rs.borderLeftWidth;
            }
            radius -= inset;
            float limit = Mathf.Min(box.width, box.height) * 0.5f;
            return Mathf.Clamp(radius, 0f, limit);
        }

        private static float Perimeter(float w, float h, float r)
        {
            return 2f * (w - 2f * r) + 2f * (h - 2f * r) + 2f * Mathf.PI * r;
        }

        /// <summary>
        /// Walks the rounded rectangle clockwise from the start of the top edge and returns the
        /// point at <paramref name="distance"/> along it.
        /// </summary>
        private static Vector2 PointAt(Rect box, float r, float perimeter, float distance)
        {
            float w = box.width;
            float h = box.height;
            float d = Mathf.Repeat(distance, perimeter);

            float straightX = Mathf.Max(0f, w - 2f * r);
            float straightY = Mathf.Max(0f, h - 2f * r);
            float arc = Mathf.PI * 0.5f * r;

            // 1. top edge, left to right
            if (d < straightX)
                return new Vector2(r + d, 0f);
            d -= straightX;

            // 2. top-right corner
            if (d < arc)
                return CornerPoint(new Vector2(w - r, r), r, -Mathf.PI * 0.5f, d / r);
            d -= arc;

            // 3. right edge, top to bottom
            if (d < straightY)
                return new Vector2(w, r + d);
            d -= straightY;

            // 4. bottom-right corner
            if (d < arc)
                return CornerPoint(new Vector2(w - r, h - r), r, 0f, d / r);
            d -= arc;

            // 5. bottom edge, right to left
            if (d < straightX)
                return new Vector2(w - r - d, h);
            d -= straightX;

            // 6. bottom-left corner
            if (d < arc)
                return CornerPoint(new Vector2(r, h - r), r, Mathf.PI * 0.5f, d / r);
            d -= arc;

            // 7. left edge, bottom to top
            if (d < straightY)
                return new Vector2(0f, h - r - d);
            d -= straightY;

            // 8. top-left corner
            return CornerPoint(new Vector2(r, r), r, Mathf.PI, d / r);
        }

        private static Vector2 CornerPoint(Vector2 centre, float r, float startAngle, float sweep)
        {
            float a = startAngle + sweep;
            return new Vector2(centre.x + Mathf.Cos(a) * r, centre.y + Mathf.Sin(a) * r);
        }
    }
}
