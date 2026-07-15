using System.Net;
using System.Net.Sockets;
using Azure.AI.AgentServer.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HarnessAgentDiagnostics.Tests;

public sealed class HostedProbeTests
{
    [Fact]
    public async Task Build_MapsRealResponsesAndReadinessEndpointsWithoutModelCall()
    {
        ProbeAgentContext context = ProbeAgentFactory.Create(
            new ThrowingChatClient(),
            "HarnessAgentDiagnostics.Tests.Hosted");
        AgentHostApp host = HostedProbe.Build(
            context.Agent,
            new Uri("http://127.0.0.1:0"));

        string[] routes = ((IEndpointRouteBuilder)host.App).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();
        Assert.Contains("/responses", routes);
        Assert.Contains("/readiness", routes);
        ILogger<AgentHostBuilder> startupLogger =
            host.App.Services.GetRequiredService<ILogger<AgentHostBuilder>>();
        Assert.False(startupLogger.IsEnabled(LogLevel.Information));

        try
        {
            await host.App.StartAsync();
            IServer server = host.App.Services.GetRequiredService<IServer>();
            string address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using HttpClient client = new();

            using HttpResponseMessage response = await client.GetAsync(new Uri(new Uri(address), "readiness"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await host.App.StopAsync();
            await host.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotPrintListeningWhenStartAsyncCannotBind()
    {
        ProbeAgentContext context = ProbeAgentFactory.Create(
            new ThrowingChatClient(),
            "HarnessAgentDiagnostics.Tests.Hosted.BindFailure");
        using TcpListener occupiedPort = new(IPAddress.Loopback, 0);
        occupiedPort.Start();
        int port = ((IPEndPoint)occupiedPort.LocalEndpoint).Port;
        StringWriter output = new();

        Exception? exception = await Record.ExceptionAsync(
            () => HostedProbe.RunAsync(
                context.Agent,
                new Uri($"http://127.0.0.1:{port}"),
                output,
                CancellationToken.None));

        Assert.NotNull(exception);
        Assert.Empty(output.ToString());
    }
}
