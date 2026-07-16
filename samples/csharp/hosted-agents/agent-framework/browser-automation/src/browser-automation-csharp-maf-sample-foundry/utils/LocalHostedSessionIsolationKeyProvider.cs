// Copyright (c) Microsoft. All rights reserved.

using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;
using Microsoft.Agents.AI.Foundry.Hosting;

namespace BrowserAutomation;

#pragma warning disable MAAI001
internal sealed class LocalHostedSessionIsolationKeyProvider : HostedSessionIsolationKeyProvider
{
    private static readonly HostedSessionContext s_localContext = new("local-development");

    public override ValueTask<HostedSessionContext?> GetKeysAsync(
        ResponseContext context,
        CreateResponse request,
        CancellationToken cancellationToken) =>
        new(s_localContext);
}
#pragma warning restore MAAI001
