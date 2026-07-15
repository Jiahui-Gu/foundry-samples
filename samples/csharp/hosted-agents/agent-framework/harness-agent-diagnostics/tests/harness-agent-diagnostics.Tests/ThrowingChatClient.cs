using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace HarnessAgentDiagnostics.Tests;

internal sealed class ThrowingChatClient : IChatClient
{
    private readonly ChatClientMetadata _metadata = new("test-provider");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Factory tests must not invoke the model.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Factory tests must not invoke the model.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? _metadata : null;

    public void Dispose()
    {
    }
}
