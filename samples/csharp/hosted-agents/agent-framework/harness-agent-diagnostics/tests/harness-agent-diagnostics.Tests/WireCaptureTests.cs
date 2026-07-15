using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HarnessAgentDiagnostics.Tests;

public sealed class WireCaptureTests
{
    private const string Secret = "wire-test-secret-value-1234567890";
    private const string ResourceId =
        "/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/private-rg/providers/Microsoft.Test/widgets/private";

    [Fact]
    public async Task CaptureAsync_UsesCredentialFreeRequestAndPreservesSanitizedFragmentedSse()
    {
        TaskCompletionSource<RequestSnapshot> requestReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            requestReceived.TrySetResult(await SnapshotAsync(context.Request));
            context.Response.ContentType = "text/event-stream";
            string stream =
                $": comment clientSecret={Secret}\r\n" +
                "id: resp_123456789\r\n" +
                "event: response.output_text.delta\r\n" +
                "retry: 1500\r\n" +
                "data: {\"type\":\"response.output_text.delta\",\"id\":\"resp_123456789\",\"delta\":\"line one\",\r\n" +
                $"data: \"resource\":\"{ResourceId}\",\"secret\":\"{Secret}\"}}\r\n\r\n" +
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_987654321\"}}\n\n" +
                "data: [DONE]\n\n";
            foreach (string fragment in Fragment(stream, 1, 2, 5, 3, 8))
            {
                await context.Response.WriteAsync(fragment);
                await context.Response.Body.FlushAsync();
            }
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(server.BaseUrl, outputDirectory);
            RequestSnapshot request = await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("POST", request.Method);
            Assert.Equal("/responses", request.Path);
            Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
            Assert.DoesNotContain(
                request.Headers.Keys,
                name => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("key", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("token", StringComparison.OrdinalIgnoreCase));
            using JsonDocument requestJson = JsonDocument.Parse(request.Body);
            Assert.True(requestJson.RootElement.GetProperty("stream").GetBoolean());
            string prompt = requestJson.RootElement.GetProperty("input").GetString()!;
            Assert.Contains("label=maf-probe; values=3,1,4", prompt, StringComparison.Ordinal);
            Assert.Contains("exactly one compute_probe call with [3,1,4]", prompt, StringComparison.Ordinal);
            Assert.Contains("plan to execute", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("explicitly authorized", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Do not use web, shell, network, or external files", prompt, StringComparison.Ordinal);

            Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
            Assert.Equal("text/event-stream", summary.ContentType);
            Assert.Equal(3, summary.EventCount);
            Assert.Equal(
                ["response.output_text.delta", "response.completed", "message"],
                summary.EventNames.ToArray());
            Assert.Equal(
                ["response.output_text.delta", "response.completed"],
                summary.DataTypes.ToArray());
            Assert.True(summary.Done);
            Assert.True(summary.Completed);
            Assert.Empty(summary.FailureMarkers);
            Assert.Empty(summary.MissingMarkers);

            string sse = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, WireCapture.SseFileName));
            string[] jsonLines = await File.ReadAllLinesAsync(
                Path.Combine(outputDirectory, WireCapture.EventsFileName));
            Assert.Contains(
                ": comment clientSecret=[REDACTED]",
                sse,
                StringComparison.Ordinal);
            Assert.True(
                sse.IndexOf("id:", StringComparison.Ordinal)
                    < sse.IndexOf("event:", StringComparison.Ordinal));
            Assert.Contains("retry: 1500", sse, StringComparison.Ordinal);
            Assert.Contains("data: [DONE]", sse, StringComparison.Ordinal);
            Assert.Equal(
                4,
                sse.Split(["\r\n", "\n"], StringSplitOptions.None)
                    .Count(line => line.StartsWith("data:", StringComparison.Ordinal)));
            Assert.Equal(3, jsonLines.Length);
            Assert.All(jsonLines, line => JsonDocument.Parse(line).Dispose());
            Assert.Contains("\"done\":true", jsonLines[2], StringComparison.Ordinal);
            using JsonDocument firstEvent = JsonDocument.Parse(jsonLines[0]);
            using JsonDocument secondEvent = JsonDocument.Parse(jsonLines[1]);
            string firstId = firstEvent.RootElement.GetProperty("id").GetString()!;
            Assert.Equal(
                firstId,
                firstEvent.RootElement.GetProperty("data").GetProperty("id").GetString());
            Assert.NotEqual(
                firstId,
                secondEvent.RootElement
                    .GetProperty("data")
                    .GetProperty("response")
                    .GetProperty("id")
                    .GetString());

            string evidence = sse + string.Join('\n', jsonLines);
            Assert.DoesNotContain(Secret, evidence, StringComparison.Ordinal);
            Assert.DoesNotContain(ResourceId, evidence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("resp_123456789", evidence, StringComparison.Ordinal);
            Assert.DoesNotContain("resp_987654321", evidence, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", evidence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("x-platform-server", evidence, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "text/plain")]
    [InlineData(HttpStatusCode.OK, "application/json")]
    public async Task CaptureAsync_RejectsBadStatusOrContentTypeWithoutOutput(
        HttpStatusCode status,
        string contentType)
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = contentType;
            await context.Response.WriteAsync($"clientSecret={Secret}");
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureException exception = await Assert.ThrowsAsync<WireCaptureException>(
                () => WireCapture.CaptureAsync(server.BaseUrl, outputDirectory));

            Assert.Equal(status, exception.StatusCode);
            Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.SseFileName)));
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.EventsFileName)));
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_FinalizesMalformedJsonAsFailedSanitizedEvidence()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                $"event: response.delta\ndata: {{\"type\":\"response.delta\",\"message\":\"clientSecret={Secret}\"\n\n");
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(server.BaseUrl, outputDirectory);

            Assert.Equal(["malformed-json"], summary.FailureMarkers.ToArray());
            Assert.False(summary.Completed);
            string evidence = await ReadEvidenceAsync(outputDirectory);
            Assert.Contains("\"failure\":\"malformed-json\"", evidence, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, evidence, StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_ReportsMissingTerminalMarkers()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n");
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(server.BaseUrl, outputDirectory);

            Assert.False(summary.Completed);
            Assert.False(summary.Done);
            Assert.Equal(
                ["missing-response-completed", "missing-done"],
                summary.MissingMarkers.ToArray());
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_CancellationRemovesPartialOutput()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                "event: response.delta\ndata: {\"type\":\"response.delta\"}\n\n");
            await context.Response.Body.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        string outputDirectory = CreateOutputDirectory();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(250));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WireCapture.CaptureAsync(server.BaseUrl, outputDirectory, cancellation.Token));

            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.SseFileName)));
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.EventsFileName)));
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_MidStreamFailureRemovesPartialOutput()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentLength = 10_000;
            await context.Response.WriteAsync(
                "event: response.delta\ndata: {\"type\":\"response.delta\"}\n\n");
            await context.Response.Body.FlushAsync();
            context.Abort();
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureException exception = await Assert.ThrowsAsync<WireCaptureException>(
                () => WireCapture.CaptureAsync(server.BaseUrl, outputDirectory));

            Assert.Contains("stream-failure", exception.Markers);
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.SseFileName)));
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.EventsFileName)));
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    private static async Task<RequestSnapshot> SnapshotAsync(HttpRequest request)
    {
        using StreamReader reader = new(request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync();
        return new RequestSnapshot(
            request.Method,
            request.Path.Value!,
            request.ContentType!,
            request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()),
            body);
    }

    private static IEnumerable<string> Fragment(string value, params int[] sizes)
    {
        int offset = 0;
        int sizeIndex = 0;
        while (offset < value.Length)
        {
            int count = Math.Min(sizes[sizeIndex++ % sizes.Length], value.Length - offset);
            yield return value.Substring(offset, count);
            offset += count;
        }
    }

    private static string CreateOutputDirectory()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "test-output",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteOutputDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<string> ReadEvidenceAsync(string outputDirectory)
        => await File.ReadAllTextAsync(Path.Combine(outputDirectory, WireCapture.SseFileName))
            + await File.ReadAllTextAsync(Path.Combine(outputDirectory, WireCapture.EventsFileName));

    private sealed record RequestSnapshot(
        string Method,
        string Path,
        string ContentType,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private LoopbackServer(WebApplication app, Uri baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        internal Uri BaseUrl { get; }

        internal static async Task<LoopbackServer> StartAsync(RequestDelegate handler)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            WebApplication app = builder.Build();
            app.MapPost("/responses", handler);
            await app.StartAsync();
            IServer server = app.Services.GetRequiredService<IServer>();
            string address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new LoopbackServer(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
