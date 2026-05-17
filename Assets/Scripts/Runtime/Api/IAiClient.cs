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
    }
}
