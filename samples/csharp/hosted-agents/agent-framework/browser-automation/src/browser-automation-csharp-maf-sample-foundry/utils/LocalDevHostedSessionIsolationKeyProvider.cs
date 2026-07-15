// Copyright (c) Microsoft. All rights reserved.

using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;
using Microsoft.Agents.AI.Foundry.Hosting;

namespace BrowserAutomation;

// HostedSessionIsolationKeyProvider and HostedSessionContext are experimental (MAAI001).
#pragma warning disable MAAI001

/// <summary>
/// Local-development <see cref="HostedSessionIsolationKeyProvider"/> that falls back to a fixed
/// user id when the platform-injected <c>x-agent-user-id</c> header is absent.
///
/// The Foundry hosting layer only sets that header when the platform hosts the container. Local
/// runs (<c>dotnet run</c>, <c>azd ai agent run</c>, <c>azd ai agent invoke --local</c>, or a plain
/// curl against <c>http://localhost:8088/responses</c> as documented in the README) don't have it,
/// so without this fallback every local request fails with a 500
/// ("HostedSessionIsolationKeyProvider returned null for the current request").
/// </summary>
internal sealed class LocalDevHostedSessionIsolationKeyProvider : HostedSessionIsolationKeyProvider
{
    private const string LocalDevUserId = "local-dev-user";

    public override ValueTask<HostedSessionContext?> GetKeysAsync(
        ResponseContext context,
        CreateResponse request,
        CancellationToken cancellationToken)
    {
        var userId = context?.PlatformContext?.UserIdKey;
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = LocalDevUserId;
        }

        return new ValueTask<HostedSessionContext?>(new HostedSessionContext(userId));
    }
}

#pragma warning restore MAAI001
