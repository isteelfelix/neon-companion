using System;
using System.Collections.Generic;
using UniVRM10;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// The emotional states the companion's face can wear. Deliberately small:
    /// each one has to read clearly at portrait framing on a model whose only
    /// guaranteed presets are the five VRM emotions.
    /// </summary>
    internal enum AvatarEmotion
    {
        Neutral = 0,
        Happy = 1,
        Sad = 2,
        Angry = 3,
        Surprised = 4,
        Confused = 5,
        Relaxed = 6,
        Shy = 7,
        Excited = 8,
        Sleepy = 9
    }

    /// <summary>One blendshape's share of an emotion.</summary>
    internal readonly struct VrmEmotionAccent
    {
        internal readonly ExpressionKey Key;
        internal readonly float Weight;

        internal VrmEmotionAccent(ExpressionKey key, float weight)
        {
            Key = key;
            Weight = weight;
        }
    }

    /// <summary>
    /// What an emotion looks like, how long it takes to arrive, and how long it
    /// outstays its trigger.
    /// <para>
    /// No accent reaches 1.0. A VRM preset at full weight is the extreme the
    /// author sculpted for a viewer poking at a slider, and it reads as a mask
    /// rather than a mood; 0.7–0.8 is where a face still looks inhabited. The
    /// same restraint leaves headroom for the accents to stack.
    /// </para>
    /// </summary>
    internal sealed class VrmEmotionPalette
    {
        private static readonly VrmEmotionAccent[] NoAccents = new VrmEmotionAccent[0];

        private static readonly Dictionary<AvatarEmotion, VrmEmotionPalette> Palettes =
            BuildPalettes();

        private static readonly Dictionary<string, AvatarEmotion> Aliases = BuildAliases();

        /// <summary>Seconds to travel from the current face to this one.</summary>
        internal readonly float BlendSeconds;

        /// <summary>
        /// Seconds this emotion holds once it has arrived, before the face drifts
        /// back to neutral on its own.
        /// </summary>
        internal readonly float HoldSeconds;

        internal readonly VrmEmotionAccent[] Accents;

        // Reactive use (emoji-driven, per-sentence) wants punchier emotions than the
        // authored 5s holds, so every hold is scaled down here in one place.
        private const float HoldScale = 0.4f;

        private VrmEmotionPalette(
            float blendSeconds,
            float holdSeconds,
            VrmEmotionAccent[] accents)
        {
            BlendSeconds = blendSeconds;
            HoldSeconds = holdSeconds * HoldScale;
            Accents = accents ?? NoAccents;
        }

        internal static VrmEmotionPalette Resolve(AvatarEmotion emotion)
        {
            VrmEmotionPalette palette;
            if (Palettes.TryGetValue(emotion, out palette))
                return palette;
            return Palettes[AvatarEmotion.Neutral];
        }

        /// <summary>
        /// Maps the loose names that reach us — agent markers, reaction strings
        /// typed into a profile — onto the enum.
        /// </summary>
        internal static bool TryParse(string name, out AvatarEmotion emotion)
        {
            emotion = AvatarEmotion.Neutral;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return Aliases.TryGetValue(name.Trim(), out emotion);
        }

        private static Dictionary<AvatarEmotion, VrmEmotionPalette> BuildPalettes()
        {
            var palettes = new Dictionary<AvatarEmotion, VrmEmotionPalette>();

            // Neutral claims nothing, so blending to it fades every accent out
            // and hands the keys back to blink and lipsync.
            palettes[AvatarEmotion.Neutral] = new VrmEmotionPalette(
                0.45f,
                0f,
                NoAccents);

            palettes[AvatarEmotion.Happy] = new VrmEmotionPalette(
                0.35f,
                5f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Happy, 0.8f),
                    new VrmEmotionAccent(ExpressionKey.Aa, 0.15f)
                });

            // Sadness arrives slowly and lingers; the lids sit a little heavy.
            palettes[AvatarEmotion.Sad] = new VrmEmotionPalette(
                0.55f,
                6.5f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Sad, 0.78f),
                    new VrmEmotionAccent(ExpressionKey.Blink, 0.15f)
                });

            palettes[AvatarEmotion.Angry] = new VrmEmotionPalette(
                0.28f,
                4f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Angry, 0.8f),
                    new VrmEmotionAccent(ExpressionKey.Ih, 0.15f)
                });

            // Surprise is the one emotion that has to be instant, and the one
            // that would look absurd if it stayed.
            palettes[AvatarEmotion.Surprised] = new VrmEmotionPalette(
                0.18f,
                1.6f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Surprised, 0.78f),
                    new VrmEmotionAccent(ExpressionKey.Aa, 0.3f)
                });

            // Neither surprise nor sadness on its own: the mix is what reads as
            // "following you, not quite there yet".
            palettes[AvatarEmotion.Confused] = new VrmEmotionPalette(
                0.4f,
                4f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Surprised, 0.35f),
                    new VrmEmotionAccent(ExpressionKey.Sad, 0.32f),
                    new VrmEmotionAccent(ExpressionKey.Ih, 0.12f)
                });

            palettes[AvatarEmotion.Relaxed] = new VrmEmotionPalette(
                0.6f,
                8f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Relaxed, 0.75f),
                    new VrmEmotionAccent(ExpressionKey.Blink, 0.18f)
                });

            palettes[AvatarEmotion.Shy] = new VrmEmotionPalette(
                0.5f,
                4.5f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Relaxed, 0.55f),
                    new VrmEmotionAccent(ExpressionKey.Sad, 0.28f),
                    new VrmEmotionAccent(ExpressionKey.Blink, 0.3f),
                    new VrmEmotionAccent(ExpressionKey.Ih, 0.1f)
                });

            palettes[AvatarEmotion.Excited] = new VrmEmotionPalette(
                0.22f,
                3.5f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Happy, 0.75f),
                    new VrmEmotionAccent(ExpressionKey.Surprised, 0.35f),
                    new VrmEmotionAccent(ExpressionKey.Aa, 0.25f)
                });

            // The heavy lid is the whole emotion, and it is why blink keys
            // resolve by Max: a reflex blink passes over it and it comes back.
            palettes[AvatarEmotion.Sleepy] = new VrmEmotionPalette(
                0.9f,
                12f,
                new VrmEmotionAccent[]
                {
                    new VrmEmotionAccent(ExpressionKey.Relaxed, 0.7f),
                    new VrmEmotionAccent(ExpressionKey.Blink, 0.6f),
                    new VrmEmotionAccent(ExpressionKey.Sad, 0.12f)
                });

            return palettes;
        }

        private static Dictionary<string, AvatarEmotion> BuildAliases()
        {
            var aliases = new Dictionary<string, AvatarEmotion>(
                StringComparer.OrdinalIgnoreCase);

            Alias(aliases, AvatarEmotion.Neutral, "neutral", "idle", "calm", "default");
            Alias(aliases, AvatarEmotion.Happy, "happy", "smile", "joy", "glad");
            Alias(aliases, AvatarEmotion.Sad, "sad", "sorrow", "unhappy");
            Alias(aliases, AvatarEmotion.Angry, "angry", "mad", "annoyed");
            Alias(aliases, AvatarEmotion.Surprised, "surprised", "surprise", "shocked");
            Alias(aliases, AvatarEmotion.Confused, "confused", "puzzled", "unsure");
            Alias(aliases, AvatarEmotion.Relaxed, "relaxed", "content", "serene");
            Alias(aliases, AvatarEmotion.Shy, "shy", "embarrassed", "bashful");
            Alias(aliases, AvatarEmotion.Excited, "excited", "cheerful", "delighted");
            Alias(aliases, AvatarEmotion.Sleepy, "sleepy", "tired", "drowsy");

            return aliases;
        }

        private static void Alias(
            Dictionary<string, AvatarEmotion> aliases,
            AvatarEmotion emotion,
            params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
                aliases[names[i]] = emotion;
        }
    }
}
