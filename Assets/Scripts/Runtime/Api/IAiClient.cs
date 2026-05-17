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

        Task SendMessageStreamAsync(
            ProviderConfig provider,
            AiChatRequest request,
            Action<string> onToken,
            CancellationToken cancellationToken = default);

        Task<ConnectionTestResult> TestConnectionAsync(
            ProviderConfig provider,
            CancellationToken cancellationToken = default);
    }
}
