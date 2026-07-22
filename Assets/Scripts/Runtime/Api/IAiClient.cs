using System;
using System.Threading;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Api.Models;
using NeonCompanion.Runtime.Data.Models;

namespace NeonCompanion.Runtime.Api
{
    public interface IAiClient
    {
        Task<AiChatResponse> SendMessageAsync(
            ProviderConfig provider,
            AiChatRequest request,
            CancellationToken cancellationToken = default);

        Task<AiChatResponse> SendMessageStreamAsync(
            ProviderConfig provider,
            AiChatRequest request,
            Action<string> onToken,
            CancellationToken cancellationToken = default,
            Action<ToolProgressInfo> onToolProgress = null);

        Task<ConnectionTestResult> TestConnectionAsync(
            ProviderConfig provider,
            CancellationToken cancellationToken = default);

        Task<ModelSwitchResult> ApplySessionModelAsync(
            ProviderConfig provider,
            string targetModel,
            string providerSessionId = null,
            CancellationToken cancellationToken = default);
    }
}
