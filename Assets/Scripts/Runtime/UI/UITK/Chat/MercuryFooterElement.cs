using UnityEngine;
using UnityEngine.UIElements;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    /// <summary>
    /// Three mercury droplets living in the assistant bubble's stats footer. They drift together,
    /// merge into one and part again; the cycle speed, how far they travel and how hard they squash
    /// all follow a single 0..1 "energy" value.
    ///
    /// Energy has two feeds and the element knows about neither: the streaming coordinator pulses it
    /// per token, and the voice player pushes the playing clip's RMS. Because token rate drifts
    /// slowly while speech amplitude jumps per syllable, the same physics reads as "working" during
    /// a stream and as "speaking" during playback — no second animation needed.
    ///
    /// At rest the droplets fuse into a single still blob and the per-frame tick unsubscribes, so a
    /// long transcript holds one static frame per message and animates only the active one.
    ///
    /// Drawn with Painter2D rather than USS: merging needs real vector work (two cubics forming the
    /// neck between droplets), and USS has neither gradients, nor keyframes, nor arbitrary paths.
    /// </summary>
    public sealed class MercuryFooterElement : VisualElement
    {
        // Geometry, in local pixels. The footer band is ~17px tall, so the droplets stay small and
        // travel horizontally — there is no vertical room to spend.
        private const float BaseRadius = 3.1f;
        private const float MaxSpread = 26f;
        private const float MergedBonus = 0.45f;

        // Energy behaviour.
        private const float DecayPerSecond = 1.35f;   // how fast an un-fed energy target falls back
        private const float Responsiveness = 7.5f;    // spring rate of the smoothed value
        private const float SleepThreshold = 0.004f;
        private const float CycleSlow = 1.35f;        // rad/s at zero energy
        private const float CycleFast = 3.9f;         // extra rad/s at full energy

        // Metaball tuning: how far apart droplets may be and still grow a neck, and how fat it is.
        private const float NeckReach = 2.55f;
        private const float NeckFatness = 2.4f;
        private const float NeckMix = 0.5f;

        private const float HalfPi = Mathf.PI * 0.5f;
        private const float EllipseKappa = 0.5522847498f;

        private float _energy;
        private float _energyTarget;
        private float _phase;
        private float _lastTime;
        private IVisualElementScheduledItem _tick;

        public MercuryFooterElement()
        {
            // Never steal hover from the bubble: the bubble's :hover drives the action buttons.
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
        }

        /// <summary>Nudge the droplets — one token, one pulse. Repeated pulses accumulate.</summary>
        public void Pulse(float amount)
        {
            _energyTarget = Mathf.Clamp01(_energyTarget + amount);
            Wake();
        }

        /// <summary>Drive energy directly, for a continuous signal such as audio RMS.</summary>
        public void SetEnergy(float value)
        {
            float clamped = Mathf.Clamp01(value);
            if (clamped > _energyTarget)
                _energyTarget = clamped;
            if (clamped > SleepThreshold)
                Wake();
        }

        /// <summary>Let the droplets coast back together; the tick stops on its own once merged.</summary>
        public void Settle()
        {
            _energyTarget = 0f;
            Wake();
        }

        private void Wake()
        {
            if (_tick != null)
                return;
            _lastTime = Time.unscaledTime;
            _tick = schedule.Execute(Step).Every(16);
        }

        private void Sleep()
        {
            if (_tick == null)
                return;
            _tick.Pause();
            _tick = null;
        }

        private void Step()
        {
            float now = Time.unscaledTime;
            // Clamp: a stalled frame (asset import, alt-tab) must not teleport the simulation.
            float dt = Mathf.Clamp(now - _lastTime, 0f, 0.05f);
            _lastTime = now;

            _energyTarget = Mathf.MoveTowards(_energyTarget, 0f, DecayPerSecond * dt);
            // Exponential approach rather than a fixed lerp: frame-rate independent, and the lag is
            // what gives the droplets their liquid weight.
            _energy = Mathf.Lerp(_energy, _energyTarget, 1f - Mathf.Exp(-Responsiveness * dt));
            _phase += dt * (CycleSlow + CycleFast * _energy);
            if (_phase > Mathf.PI * 2f)
                _phase -= Mathf.PI * 2f;

            MarkDirtyRepaint();

            if (_energy < SleepThreshold && _energyTarget < SleepThreshold)
            {
                // Snap to the merged pose so the frame we leave behind is the clean idle one.
                _energy = 0f;
                _energyTarget = 0f;
                MarkDirtyRepaint();
                Sleep();
            }
        }

        private void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            Rect box = contentRect;
            if (box.width <= 2f || box.height <= 2f)
                return;

            float cx = box.width * 0.5f;
            float cy = box.height * 0.5f;

            // cos == -1 -> fully merged, cos == +1 -> fully apart. Energy scales the whole travel,
            // so at rest the droplets sit on top of each other whatever the phase happens to be.
            float openness = (1f - Mathf.Cos(_phase)) * 0.5f;
            float spread = MaxSpread * _energy * openness;

            // Squash and stretch: fastest at mid-travel, which is where sin peaks.
            float speed = Mathf.Abs(Mathf.Sin(_phase)) * _energy;
            float stretch = 1f + 0.34f * speed;
            float flatten = 1f / stretch;

            float sideRadius = BaseRadius;
            // The merged blob reads as one heavier droplet rather than three stacked ones.
            float coreRadius = BaseRadius * (1f + MergedBonus * (1f - openness * _energy));

            Vector2 left = new Vector2(cx - spread, cy);
            Vector2 core = new Vector2(cx, cy);
            Vector2 right = new Vector2(cx + spread, cy);

            float sideRx = sideRadius * stretch;
            float sideRy = sideRadius * flatten;
            float coreRx = coreRadius * (1f + 0.12f * speed);
            float coreRy = coreRadius * (1f - 0.10f * speed);

            var painter = ctx.painter2D;
            painter.fillColor = resolvedStyle.color;

            // One path, one fill, NonZero: overlapping droplets and necks merge into a single
            // silhouette instead of showing seams where translucent fills cross.
            painter.BeginPath();
            AddEllipse(painter, left, sideRx, sideRy);
            AddEllipse(painter, core, coreRx, coreRy);
            AddEllipse(painter, right, sideRx, sideRy);
            AddNeck(painter, left, (sideRx + sideRy) * 0.5f, core, (coreRx + coreRy) * 0.5f);
            AddNeck(painter, core, (coreRx + coreRy) * 0.5f, right, (sideRx + sideRy) * 0.5f);
            painter.Fill(FillRule.NonZero);
        }

        /// <summary>
        /// Painter2D.Arc only draws circles, and squashed droplets are the whole point — so the
        /// ellipse is four cubics with the standard circle-to-bezier constant.
        /// </summary>
        private static void AddEllipse(Painter2D painter, Vector2 c, float rx, float ry)
        {
            if (rx <= 0.01f || ry <= 0.01f)
                return;

            float ox = rx * EllipseKappa;
            float oy = ry * EllipseKappa;

            painter.MoveTo(new Vector2(c.x - rx, c.y));
            painter.BezierCurveTo(new Vector2(c.x - rx, c.y - oy), new Vector2(c.x - ox, c.y - ry), new Vector2(c.x, c.y - ry));
            painter.BezierCurveTo(new Vector2(c.x + ox, c.y - ry), new Vector2(c.x + rx, c.y - oy), new Vector2(c.x + rx, c.y));
            painter.BezierCurveTo(new Vector2(c.x + rx, c.y + oy), new Vector2(c.x + ox, c.y + ry), new Vector2(c.x, c.y + ry));
            painter.BezierCurveTo(new Vector2(c.x - ox, c.y + ry), new Vector2(c.x - rx, c.y + oy), new Vector2(c.x - rx, c.y));
            painter.ClosePath();
        }

        /// <summary>
        /// The bridge between two droplets that are close but not yet overlapping: tangent points on
        /// both circles joined by two cubics, which is what sells the surface tension. Standard
        /// metaball construction; returns without drawing when they are too far apart, or when one
        /// already swallows the other and the ellipses alone cover the shape.
        /// </summary>
        private static void AddNeck(Painter2D painter, Vector2 c1, float r1, Vector2 c2, float r2)
        {
            if (r1 <= 0.01f || r2 <= 0.01f)
                return;

            float d = Vector2.Distance(c1, c2);
            if (d < 0.001f)
                return;
            if (d > (r1 + r2) * NeckReach)
                return;
            if (d <= Mathf.Abs(r1 - r2))
                return;

            float u1 = 0f;
            float u2 = 0f;
            if (d < r1 + r2)
            {
                u1 = Mathf.Acos(Mathf.Clamp((r1 * r1 + d * d - r2 * r2) / (2f * r1 * d), -1f, 1f));
                u2 = Mathf.Acos(Mathf.Clamp((r2 * r2 + d * d - r1 * r1) / (2f * r2 * d), -1f, 1f));
            }

            float axis = Mathf.Atan2(c2.y - c1.y, c2.x - c1.x);
            float spreadAngle = Mathf.Acos(Mathf.Clamp((r1 - r2) / d, -1f, 1f));

            float a1a = axis + u1 + (spreadAngle - u1) * NeckMix;
            float a1b = axis - u1 - (spreadAngle - u1) * NeckMix;
            float a2a = axis + Mathf.PI - u2 - (Mathf.PI - u2 - spreadAngle) * NeckMix;
            float a2b = axis - Mathf.PI + u2 + (Mathf.PI - u2 - spreadAngle) * NeckMix;

            Vector2 p1a = c1 + Polar(a1a, r1);
            Vector2 p1b = c1 + Polar(a1b, r1);
            Vector2 p2a = c2 + Polar(a2a, r2);
            Vector2 p2b = c2 + Polar(a2b, r2);

            float totalRadius = r1 + r2;
            float handle = Mathf.Min(NeckMix * NeckFatness, Vector2.Distance(p1a, p2a) / totalRadius);
            handle *= Mathf.Min(1f, d * 2f / totalRadius);

            float h1 = r1 * handle;
            float h2 = r2 * handle;

            Vector2 s1 = p1a + Polar(a1a - HalfPi, h1);
            Vector2 s2 = p2a + Polar(a2a + HalfPi, h2);
            Vector2 s3 = p2b + Polar(a2b - HalfPi, h2);
            Vector2 s4 = p1b + Polar(a1b + HalfPi, h1);

            painter.MoveTo(p1a);
            painter.BezierCurveTo(s1, s2, p2a);
            painter.LineTo(p2b);
            painter.BezierCurveTo(s3, s4, p1b);
            painter.ClosePath();
        }

        private static Vector2 Polar(float angle, float radius)
        {
            return new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
        }
    }
}
