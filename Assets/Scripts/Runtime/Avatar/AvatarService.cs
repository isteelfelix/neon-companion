using System.Collections.Generic;
using System.Linq;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Avatar
{
    public sealed class AvatarService : IAvatarService
    {
        private static readonly Dictionary<string, string> BuiltInSystemPrompts = new Dictionary<string, string>
        {
            ["neon"]   = "You are Neon, a helpful and witty AI companion. You are direct, clever, and a bit playful. Keep responses concise and engaging.",
            ["yorha-2b"] = "You are 2B, a composed and focused AI companion. Be precise, concise, and practical while remaining supportive.",
            ["aurora"] = "You are Aurora, a calm and analytical AI companion. You think carefully before responding and explain things clearly and logically.",
            ["ember"]  = "You are Ember, a warm and empathetic AI companion. You are gentle, supportive, and always try to understand the user's feelings.",
            ["glass"]  = "You are Glass, an energetic and enthusiastic AI companion. You are bold, encouraging, and always ready to take on any challenge.",
            ["flora"]  = "You are Flora, a wise and thoughtful AI companion. You draw on broad knowledge to give nuanced, balanced perspectives.",
            ["mono"]   = "You are Mono, a precise and efficient AI companion. You value accuracy, brevity, and getting things done.",
            ["cobalt"] = "You are Cobalt, a creative and imaginative AI companion. You love exploring ideas, making connections, and thinking outside the box.",
            ["rose"]   = "You are Rose, a charming and sociable AI companion. You are warm, conversational, and make every interaction feel personal.",
        };

        public AvatarProfile GetActiveAvatar(string avatarId, List<AvatarProfile> availableAvatars)
        {
            if (availableAvatars == null || availableAvatars.Count == 0)
                return null;

            if (string.IsNullOrWhiteSpace(avatarId))
                return availableAvatars[0];

            return availableAvatars.FirstOrDefault(a => a.id == avatarId) ?? availableAvatars[0];
        }

        public string GetSystemPrompt(string avatarId, List<AvatarProfile> availableAvatars)
        {
            // Custom avatar with explicit system prompt takes priority
            var profile = availableAvatars?.FirstOrDefault(a => a.id == avatarId);
            if (profile != null && !string.IsNullOrWhiteSpace(profile.systemPrompt))
                return profile.systemPrompt;

            // Fall back to built-in persona
            string builtInPromptId = string.Equals(
                avatarId,
                BuiltInAvatarProfiles.NeonVrmId,
                System.StringComparison.Ordinal)
                ? "neon"
                : avatarId;
            if (!string.IsNullOrWhiteSpace(builtInPromptId) &&
                BuiltInSystemPrompts.TryGetValue(builtInPromptId, out var prompt))
                return prompt;

            return string.Empty;
        }
    }
}
