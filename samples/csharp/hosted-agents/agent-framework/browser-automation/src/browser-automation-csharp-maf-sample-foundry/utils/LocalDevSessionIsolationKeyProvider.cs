// Copyright (c) Microsoft. All rights reserved.

using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;
using Microsoft.Agents.AI.Foundry.Hosting;

namespace BrowserAutomation;

/// <summary>
/// Fallback <see cref="HostedSessionIsolationKeyProvider"/> that supplies a stable local identity
/// when the platform-injected <c>x-agent-user-id</c> header is absent.
/// </summary>
/// <remarks>
/// The default provider returns <see langword="null"/> when no <c>x-agent-user-id</c> header is
/// present, which the hosting layer treats as a hard error whenever it resolves the request as
/// running inside Foundry. That resolution can trigger during local development (e.g. `dotnet run`,
/// `docker run`, or `azd ai agent run`) even though no platform header will ever be supplied outside
/// the Foundry runtime, turning every local invocation into an HTTP 500. Registering this fallback
/// (as suggested by the hosting layer's own error message) keeps local runs working by using a fixed
/// local identity whenever the platform doesn't supply one, while still honoring the real
/// platform-supplied user id when present.
/// </remarks>
#pragma warning disable MAAI001 // HostedSessionIsolationKeyProvider/HostedSessionContext are experimental
internal sealed class LocalDevSessionIsolationKeyProvider : HostedSessionIsolationKeyProvider
{
    private const string LocalDevUserId = "local-dev";

    /// <inheritdoc />
    public override ValueTask<HostedSessionContext?> GetKeysAsync(
        ResponseContext context,
        CreateResponse request,
        CancellationToken cancellationToken)
    {
        var userKey = context?.PlatformContext?.UserIdKey;
        var resolvedUserId = string.IsNullOrWhiteSpace(userKey) ? LocalDevUserId : userKey!;
        return new ValueTask<HostedSessionContext?>(new HostedSessionContext(resolvedUserId));
    }
}
#pragma warning restore MAAI001
