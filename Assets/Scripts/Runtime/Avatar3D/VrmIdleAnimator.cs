using UnityEngine;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// The involuntary motion of an idle face: blinks that punctuate, and
    /// saccades — the small, constant eye darts that stop a gaze from looking
    /// painted on. Two signals a still portrait is missing and the eye notices
    /// at once.
    /// <para>
    /// Pure signal generation. It advances timers and reports a blink weight and
    /// a gaze offset; the driver decides where those numbers go. Randomness comes
    /// from an injected <see cref="System.Random"/>, so a test can pin the cadence
    /// and production can leave it to chance.
    /// </para>
    /// </summary>
    internal sealed class VrmIdleAnimator
    {
        // The lid falls faster than it lifts — the way a real blink moves — and
        // both halves are quick enough to read as a blink rather than a wink.
        private const float BlinkCloseSeconds = 0.06f;
        private const float BlinkOpenSeconds = 0.12f;

        internal const float BlinkIntervalMin = 2.5f;
        internal const float BlinkIntervalMax = 5.5f;

        // Small, because a saccade is the eye settling rather than looking
        // somewhere; frequent, because a motionless eye is the first thing that
        // reads as dead. The offset is normalized gaze, layered on top of
        // whatever the gaze command already asked for.
        internal const float SaccadeAmplitude = 0.12f;
        private const float SaccadeHoldMin = 0.35f;
        private const float SaccadeHoldMax = 1.6f;
        private const float SaccadeSpeed = 14f;

        private readonly System.Random _random;

        private float _blinkInterval;
        private float _blinkSinceLast;
        private bool _blinking;
        private float _blinkProgress;

        private float _saccadeHold;
        private float _saccadeSinceLast;
        private float _saccadeTargetHorizontal;
        private float _saccadeTargetVertical;

        /// <summary>Blink strength this frame, 0 (open) to 1 (shut).</summary>
        internal float BlinkWeight { get; private set; }

        /// <summary>Normalized horizontal gaze jitter this frame.</summary>
        internal float GazeOffsetHorizontal { get; private set; }

        /// <summary>Normalized vertical gaze jitter this frame.</summary>
        internal float GazeOffsetVertical { get; private set; }

        internal VrmIdleAnimator(System.Random random)
        {
            _random = random != null ? random : new System.Random();
            _blinkInterval = NextRange(BlinkIntervalMin, BlinkIntervalMax);
            _saccadeHold = NextRange(SaccadeHoldMin, SaccadeHoldMax);
            PickSaccadeTarget();
        }

        internal void Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
                return;
            TickBlink(deltaTime);
            TickSaccade(deltaTime);
        }

        private void TickBlink(float deltaTime)
        {
            if (_blinking)
            {
                _blinkProgress += deltaTime;
                float duration = BlinkCloseSeconds + BlinkOpenSeconds;
                if (_blinkProgress >= duration)
                {
                    // Lid fully back up. Report a clean zero for one frame so the
                    // driver can release the key, then wait out the next interval.
                    _blinking = false;
                    _blinkProgress = 0f;
                    _blinkSinceLast = 0f;
                    _blinkInterval = NextRange(BlinkIntervalMin, BlinkIntervalMax);
                    BlinkWeight = 0f;
                    return;
                }

                BlinkWeight = BlinkShape(_blinkProgress);
                return;
            }

            _blinkSinceLast += deltaTime;
            if (_blinkSinceLast >= _blinkInterval)
            {
                _blinking = true;
                _blinkProgress = 0f;
                BlinkWeight = BlinkShape(0f);
            }
        }

        /// <summary>
        /// A smooth close then a smooth open — smoothstep on each half rather than
        /// the straight ramps of a triangle wave, so the lid eases into the shut
        /// position instead of arriving at it head-on.
        /// </summary>
        private static float BlinkShape(float progress)
        {
            if (progress < BlinkCloseSeconds)
                return SmoothStep(progress / BlinkCloseSeconds);

            float opening = (progress - BlinkCloseSeconds) / BlinkOpenSeconds;
            return 1f - SmoothStep(opening);
        }

        private void TickSaccade(float deltaTime)
        {
            _saccadeSinceLast += deltaTime;
            if (_saccadeSinceLast >= _saccadeHold)
            {
                _saccadeSinceLast = 0f;
                _saccadeHold = NextRange(SaccadeHoldMin, SaccadeHoldMax);
                PickSaccadeTarget();
            }

            // Frame-rate-independent approach to the current target: the eye
            // flicks toward the new fixation and holds there, it does not glide
            // at a constant rate.
            float t = 1f - Mathf.Exp(-SaccadeSpeed * deltaTime);
            GazeOffsetHorizontal = Mathf.Lerp(
                GazeOffsetHorizontal, _saccadeTargetHorizontal, t);
            GazeOffsetVertical = Mathf.Lerp(
                GazeOffsetVertical, _saccadeTargetVertical, t);
        }

        private void PickSaccadeTarget()
        {
            _saccadeTargetHorizontal = NextSigned() * SaccadeAmplitude;

            // Half the vertical range: eyes rove side to side far more than up
            // and down, and a portrait exaggerates any vertical wander.
            _saccadeTargetVertical = NextSigned() * SaccadeAmplitude * 0.5f;
        }

        private float NextRange(float min, float max)
        {
            return min + (float)_random.NextDouble() * (max - min);
        }

        private float NextSigned()
        {
            return (float)(_random.NextDouble() * 2.0 - 1.0);
        }

        private static float SmoothStep(float x)
        {
            if (x <= 0f)
                return 0f;
            if (x >= 1f)
                return 1f;
            return x * x * (3f - 2f * x);
        }
    }
}
