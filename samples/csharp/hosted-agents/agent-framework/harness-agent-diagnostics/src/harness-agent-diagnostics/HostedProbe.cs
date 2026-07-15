using System.Net;
using Azure.AI.AgentServer.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HarnessAgentDiagnostics;

internal static class HostedProbe
{
    internal static AgentHostApp Build(AIAgent agent, Uri url)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(url);
        Uri validatedUrl = LoopbackHttpUrl.Parse(url.AbsoluteUri);
        IPAddress address = validatedUrl.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.Parse(validatedUrl.Host);

        AgentHostBuilder builder = AgentHost.CreateBuilder([]);
        builder.WebApplicationBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<IOptionsFactory<KestrelServerOptions>>(services =>
            new LoopbackKestrelOptionsFactory(services, address, validatedUrl.Port));
        builder.Services.AddFoundryResponses(agent);
        builder.RegisterProtocol(
            "responses",
            endpoints => endpoints.MapFoundryResponses());
        return builder.Build();
    }

    internal static async Task RunAsync(
        AIAgent agent,
        Uri url,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        AgentHostApp host = Build(agent, url);
        try
        {
            await output.WriteLineAsync(
                $"Listening locally at {url.GetLeftPart(UriPartial.Authority)}")
                .ConfigureAwait(false);
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await host.App.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class LoopbackKestrelOptionsFactory(
        IServiceProvider services,
        IPAddress address,
        int port) : IOptionsFactory<KestrelServerOptions>
    {
        public KestrelServerOptions Create(string name)
        {
            KestrelServerOptions options = new()
            {
                ApplicationServices = services,
            };
            options.Listen(
                address,
                port,
                listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
            return options;
        }
    }
}
