using System;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api;
using NeonCompanion.Runtime.Chat;
using NeonCompanion.Runtime.Core;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.UI.UITK.Chat
{
    internal static class ChatContextWindowEstimator
    {
        internal static int EstimateSessionTokens(
            Func<Task<CompanionApp>> getAppAsync,
            Func<Task<ChatService>> getChatServiceAsync)
        {
            try
            {
                var app = getAppAsync().Result;
                var client = app != null ? app.AiClient as OpenAiCompatibleClient : null;
                if (client != null)
                {
                    var usage = client.LastStreamUsage;
                    if (usage.prompt_tokens > 0)
                        return usage.prompt_tokens;
                }
            }
            catch
            {
                // Fall back to the character-based estimate below.
            }

            var chat = getChatServiceAsync().Result;
            var vm = chat != null ? chat.CurrentChatViewModel : null;
            if (vm == null || vm.Messages.Count == 0)
                return 0;

            int totalChars = 0;
            for (int i = 0; i < vm.Messages.Count; i++)
            {
                var msg = vm.Messages[i];
                if (msg == null)
                    continue;
                if (!string.IsNullOrEmpty(msg.content))
                    totalChars += msg.content.Length;
                if (msg.attachments != null)
                    totalChars += msg.attachments.Count * 400;
            }
            return totalChars / 3;
        }

        internal static int GuessContextWindow(ProviderConfig provider, string selectedModel = null)
        {
            string modelId = !string.IsNullOrEmpty(selectedModel) ? selectedModel
                : (provider != null ? provider.defaultModel : null);

            if (string.IsNullOrEmpty(modelId))
                return 8192;

            string model = modelId.ToLowerInvariant();
            if (model.Contains("gpt-4o") || model.Contains("gpt-4-turbo") || model.Contains("claude-3") || model.Contains("gemini-1.5"))
                return 128000;
            if (model.Contains("llama-3") || model.Contains("llama3") || model.Contains("llama_3"))
                return 131072;
            if (model.Contains("hermes"))
                return 131072;
            if (model.Contains("mistral") || model.Contains("mixtral"))
                return 32768;
            if (model.Contains("gpt-4") || model.Contains("claude"))
                return 8192;
            if (model.Contains("gpt-3.5"))
                return 16384;
            if (model.Contains("phi") || model.Contains("gemma"))
                return 4096;
            return 8192;
        }
    }
}
