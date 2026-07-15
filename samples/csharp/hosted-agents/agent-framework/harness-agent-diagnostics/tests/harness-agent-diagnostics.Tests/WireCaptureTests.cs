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
    private const string OwnedArtifactSignature =
        "HarnessAgentDiagnostics.WireCapture.OwnedArtifact/v1";
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
                $"data: \"resource\":\"{ResourceId}\",\"apiKey\":\"{Secret}\"}}\r\n\r\n" +
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
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_StructurallySanitizesValidJsonErrorBodyOnStderr()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            string body = $"{{\"error\":{{\"apiKey\":\"{Secret}\",\"password\":\"{Secret}\"}}}}";
            foreach (string fragment in Fragment(body, 1, 3, 2))
            {
                await context.Response.WriteAsync(fragment);
                await context.Response.Body.FlushAsync();
            }
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            (int exitCode, string stderr) = await CaptureThroughApplicationAsync(
                server.BaseUrl,
                outputDirectory);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("\"apiKey\":\"[REDACTED]\"", stderr, StringComparison.Ordinal);
            Assert.Contains("\"password\":\"[REDACTED]\"", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.SseFileName)));
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.EventsFileName)));
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_UsesOnlySafeMarkerForMalformedJsonErrorBodyOnStderr()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json";
            string body = $"{{\"apiKey\":\"{Secret}\"";
            foreach (string fragment in Fragment(body, 2, 1, 4))
            {
                await context.Response.WriteAsync(fragment);
                await context.Response.Body.FlushAsync();
            }
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            (int exitCode, string stderr) = await CaptureThroughApplicationAsync(
                server.BaseUrl,
                outputDirectory);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("\"bodyType\":\"unparseable-json\"", stderr, StringComparison.Ordinal);
            Assert.Contains("\"redacted\":true", stderr, StringComparison.Ordinal);
            Assert.Contains("\"length\":", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("apiKey", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Secret, stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.SseFileName)));
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.EventsFileName)));
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public void CreateHttpHandler_DisablesAmbientProxyAndCredentialBehavior()
    {
        using SocketsHttpHandler handler = WireCapture.CreateHttpHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
    }

    [Fact]
    public async Task CaptureAsync_AppliesEventSourceFramingAndPreservesEverySanitizedFieldLine()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            string stream =
                ": state-only comment\n" +
                "id: persisted-id\n" +
                "retry: 2500\n\n" +
                "event: ignored-event\n\n" +
                "event: overwritten-event\n" +
                "event:\n" +
                $"id: ignored\0apiKey={Secret}\n" +
                "retry: -1\n" +
                "retry: invalid\n" +
                "retry: 0007\n" +
                ": first event comment\n" +
                ": second event comment\n" +
                "data: {\"type\":\"response.output_text.delta\",\n" +
                "data: \"delta\":\"line one\"}\n\n" +
                "event: ignored-completion-name\n" +
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\"}\n\n" +
                "data: [DONE]\n\n";
            foreach (string fragment in Fragment(stream, 1, 2, 5, 3))
            {
                await context.Response.WriteAsync(fragment);
                await context.Response.Body.FlushAsync();
            }
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(
                server.BaseUrl,
                outputDirectory);

            Assert.Equal(3, summary.EventCount);
            Assert.Equal(["message", "response.completed", "message"], summary.EventNames.ToArray());
            Assert.True(summary.Completed);
            Assert.True(summary.Done);
            Assert.Empty(summary.FailureMarkers);
            Assert.Empty(summary.MissingMarkers);

            string sse = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, WireCapture.SseFileName));
            string[] jsonLines = await File.ReadAllLinesAsync(
                Path.Combine(outputDirectory, WireCapture.EventsFileName));
            Assert.Equal(3, jsonLines.Length);
            using JsonDocument firstEvent = JsonDocument.Parse(jsonLines[0]);
            Assert.Equal("persisted-id", firstEvent.RootElement.GetProperty("id").GetString());
            Assert.Equal("0007", firstEvent.RootElement.GetProperty("retry").GetString());
            Assert.Equal("message", firstEvent.RootElement.GetProperty("event").GetString());
            Assert.Equal(
                "line one",
                firstEvent.RootElement.GetProperty("data").GetProperty("delta").GetString());
            Assert.Equal(
                ["first event comment", "second event comment"],
                firstEvent.RootElement.GetProperty("comments")
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .ToArray());

            Assert.Equal(1, CountOccurrences(sse, "event: ignored-event"));
            Assert.Equal(1, CountOccurrences(sse, "event: overwritten-event"));
            Assert.Equal(
                1,
                sse.Split(["\r\n", "\n"], StringSplitOptions.None)
                    .Count(line => line == "event: "));
            Assert.Equal(1, CountOccurrences(sse, "event: ignored-completion-name"));
            Assert.Equal(1, CountOccurrences(sse, "event: response.completed"));
            Assert.Contains("id: [IGNORED_NUL_ID]", sse, StringComparison.Ordinal);
            Assert.Contains("retry: -1", sse, StringComparison.Ordinal);
            Assert.Contains("retry: invalid", sse, StringComparison.Ordinal);
            Assert.Contains(": state-only comment", sse, StringComparison.Ordinal);
            Assert.Contains(": first event comment", sse, StringComparison.Ordinal);
            Assert.Contains(": second event comment", sse, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, sse, StringComparison.Ordinal);
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_PreservesUnterminatedDeltaWithoutDispatchingOrAddingDelimiter()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\"}\n\n" +
                "event: response.output_text.delta\n" +
                $"data: {{\"type\":\"response.output_text.delta\",\"apiKey\":\"{Secret}\"}}");
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(
                server.BaseUrl,
                outputDirectory);

            Assert.Equal(1, summary.EventCount);
            Assert.Equal(["response.completed"], summary.EventNames.ToArray());
            Assert.Equal(["response.completed"], summary.DataTypes.ToArray());
            Assert.True(summary.Completed);
            Assert.False(summary.Done);

            string sse = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, WireCapture.SseFileName));
            string[] jsonLines = await File.ReadAllLinesAsync(
                Path.Combine(outputDirectory, WireCapture.EventsFileName));
            Assert.Contains("event: response.output_text.delta", sse, StringComparison.Ordinal);
            Assert.Contains("\"apiKey\":\"[REDACTED]\"", sse, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, sse, StringComparison.Ordinal);
            Assert.False(
                sse.EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal));
            Assert.Single(jsonLines);
            Assert.DoesNotContain(
                "response.output_text.delta",
                jsonLines[0],
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_PreservesUnterminatedDoneWithoutDispatchingOrAddingDelimiter()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\"}\n\n" +
                "data: [DONE]");
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(
                server.BaseUrl,
                outputDirectory);

            Assert.Equal(1, summary.EventCount);
            Assert.Equal(["response.completed"], summary.EventNames.ToArray());
            Assert.True(summary.Completed);
            Assert.False(summary.Done);

            string sse = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, WireCapture.SseFileName));
            string[] jsonLines = await File.ReadAllLinesAsync(
                Path.Combine(outputDirectory, WireCapture.EventsFileName));
            Assert.Contains("data: [DONE]", sse, StringComparison.Ordinal);
            Assert.False(
                sse.EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal));
            Assert.Single(jsonLines);
            Assert.DoesNotContain("[DONE]", jsonLines[0], StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_FinalizesMalformedJsonOnlyAsFailedSafeMarkerEvidence()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            string stream =
                $"event: response.delta\ndata: {{\"type\":\"response.delta\",\"apiKey\":\"{Secret}\"\n\n";
            foreach (string fragment in Fragment(stream, 1, 4, 2))
            {
                await context.Response.WriteAsync(fragment);
                await context.Response.Body.FlushAsync();
            }
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(server.BaseUrl, outputDirectory);

            Assert.Equal(["malformed-json"], summary.FailureMarkers.ToArray());
            Assert.False(summary.Completed);
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.SseFileName)));
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.EventsFileName)));
            string evidence = await ReadFailedEvidenceAsync(outputDirectory);
            Assert.Contains("\"failure\":\"malformed-json\"", evidence, StringComparison.Ordinal);
            Assert.Contains("\"dataType\":\"unparseable-data\"", evidence, StringComparison.Ordinal);
            Assert.Contains("\"redacted\":true", evidence, StringComparison.Ordinal);
            Assert.Contains("\"length\":", evidence, StringComparison.Ordinal);
            Assert.Contains("\"recordType\":\"failure-summary\"", evidence, StringComparison.Ordinal);
            Assert.DoesNotContain("apiKey", evidence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Secret, evidence, StringComparison.Ordinal);
            AssertNoTemporaryFiles(outputDirectory);
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
        await SeedStaleSuccessFilesAsync(outputDirectory);

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(server.BaseUrl, outputDirectory);

            Assert.False(summary.Completed);
            Assert.False(summary.Done);
            Assert.Equal(
                ["missing-response-completed"],
                summary.MissingMarkers.ToArray());
            Assert.Equal("stale-successstale-success", await ReadEvidenceAsync(outputDirectory));
            string evidence = await ReadFailedEvidenceAsync(outputDirectory);
            Assert.Contains("missing-response-completed", evidence, StringComparison.Ordinal);
            Assert.Contains("\"recordType\":\"failure-summary\"", evidence, StringComparison.Ordinal);
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_CancellationLeavesPreviousBundleUntouched()
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
        await SeedStaleSuccessFilesAsync(outputDirectory);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(250));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WireCapture.CaptureAsync(server.BaseUrl, outputDirectory, cancellation.Token));

            Assert.Equal("stale-successstale-success", await ReadEvidenceAsync(outputDirectory));
            Assert.False(Directory.Exists(FailedBundleDirectory(outputDirectory)));
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_MidStreamFailureLeavesPreviousBundleUntouched()
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
        await SeedStaleSuccessFilesAsync(outputDirectory);

        try
        {
            WireCaptureException exception = await Assert.ThrowsAsync<WireCaptureException>(
                () => WireCapture.CaptureAsync(server.BaseUrl, outputDirectory));

            Assert.Contains("stream-failure", exception.Markers);
            Assert.Equal("stale-successstale-success", await ReadEvidenceAsync(outputDirectory));
            Assert.False(Directory.Exists(FailedBundleDirectory(outputDirectory)));
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_AtomicallyReplacesStaleSuccessFilesAfterValidatedCompletionWithoutDone()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            string stream =
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\"}\n\n";
            foreach (string fragment in Fragment(stream, 2, 1, 5))
            {
                await context.Response.WriteAsync(fragment);
                await context.Response.Body.FlushAsync();
            }
        });
        string outputDirectory = CreateOutputDirectory();
        await SeedStaleSuccessFilesAsync(outputDirectory);

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(
                server.BaseUrl,
                outputDirectory);

            Assert.True(summary.Completed);
            Assert.False(summary.Done);
            Assert.Empty(summary.FailureMarkers);
            Assert.Empty(summary.MissingMarkers);
            string evidence = await ReadEvidenceAsync(outputDirectory);
            Assert.DoesNotContain("stale-success", evidence, StringComparison.Ordinal);
            Assert.Contains("response.completed", evidence, StringComparison.Ordinal);
            Assert.False(Directory.Exists(FailedBundleDirectory(outputDirectory)));
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_TreatsOutputDirectoryAsDedicatedCaptureBundle()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\"}\n\n");
        });
        string outputDirectory = CreateOutputDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "old-capture-only.txt"),
            "the whole directory is replaced");

        try
        {
            await WireCapture.CaptureAsync(server.BaseUrl, outputDirectory);

            Assert.Equal(
                [WireCapture.EventsFileName, WireCapture.SseFileName],
                Directory.EnumerateFiles(outputDirectory)
                    .Select(path => Path.GetFileName(path)!)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_RestoresPreviousBundleWhenPublishIsInterruptedBetweenDirectoryMoves()
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                "event: response.completed\n" +
                "data: {\"type\":\"response.completed\"}\n\n");
        });
        string outputDirectory = CreateOutputDirectory();
        await SeedStaleSuccessFilesAsync(outputDirectory);
        bool interruptionReached = false;

        try
        {
            IOException exception = await Assert.ThrowsAsync<IOException>(
                () => WireCapture.CaptureAsync(
                    server.BaseUrl,
                    outputDirectory,
                    checkpoint =>
                    {
                        Assert.Equal(
                            WireCapturePublishCheckpoint.PreviousBundleMovedToBackup,
                            checkpoint);
                        interruptionReached = true;
                        Assert.False(Directory.Exists(outputDirectory));
                        string stagingDirectory = Assert.Single(
                            EnumerateSiblingArtifacts(outputDirectory, ".staging."));
                        Assert.True(
                            File.Exists(Path.Combine(stagingDirectory, WireCapture.SseFileName)));
                        Assert.True(
                            File.Exists(Path.Combine(stagingDirectory, WireCapture.EventsFileName)));
                        throw new IOException("Injected publish interruption.");
                    },
                    CancellationToken.None));

            Assert.True(interruptionReached);
            Assert.Equal("Injected publish interruption.", exception.Message);
            Assert.Equal("stale-successstale-success", await ReadEvidenceAsync(outputDirectory));
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task CaptureAsync_RecoversOwnedBackupAndCleansOwnedStagingWithoutDeletingLookalikes()
    {
        TaskCompletionSource responseStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.FlushAsync();
            responseStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        string outputDirectory = CreateOutputDirectory();
        await SeedStaleSuccessFilesAsync(outputDirectory);
        string staleBackup = outputDirectory + ".backup." + Guid.NewGuid().ToString("N");
        await MarkOwnedArtifactAsync(staleBackup);
        Directory.Move(outputDirectory, staleBackup);
        string staleStaging = outputDirectory + ".staging." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staleStaging);
        await File.WriteAllTextAsync(Path.Combine(staleStaging, "partial"), "partial");
        await MarkOwnedArtifactAsync(staleStaging);
        string unrelatedLookalike = outputDirectory + ".staging." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(unrelatedLookalike);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));

        try
        {
            Task capture = WireCapture.CaptureAsync(
                server.BaseUrl,
                outputDirectory,
                cancellation.Token);
            await responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);

            Assert.Equal("stale-successstale-success", await ReadEvidenceAsync(outputDirectory));
            Assert.False(Directory.Exists(staleBackup));
            Assert.False(File.Exists(OwnedArtifactMarker(staleBackup)));
            Assert.False(Directory.Exists(staleStaging));
            Assert.False(File.Exists(OwnedArtifactMarker(staleStaging)));
            Assert.True(Directory.Exists(unrelatedLookalike));
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidTerminalStreams))]
    public async Task CaptureAsync_RejectsInvalidTerminalOrdering(
        string stream,
        string expectedMarker)
    {
        await using LoopbackServer server = await LoopbackServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            foreach (string fragment in Fragment(stream, 1, 3, 2, 5))
            {
                await context.Response.WriteAsync(fragment);
                await context.Response.Body.FlushAsync();
            }
        });
        string outputDirectory = CreateOutputDirectory();

        try
        {
            WireCaptureSummary summary = await WireCapture.CaptureAsync(
                server.BaseUrl,
                outputDirectory);

            Assert.Contains(expectedMarker, summary.FailureMarkers);
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.SseFileName)));
            Assert.False(File.Exists(Path.Combine(outputDirectory, WireCapture.EventsFileName)));
            string evidence = await ReadFailedEvidenceAsync(outputDirectory);
            Assert.Contains(expectedMarker, evidence, StringComparison.Ordinal);
            Assert.Contains("\"recordType\":\"failure-summary\"", evidence, StringComparison.Ordinal);
            AssertNoTemporaryFiles(outputDirectory);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    public static IEnumerable<object[]> InvalidTerminalStreams()
    {
        yield return
        [
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\"}\n\n" +
            "data: {\"type\":\"response.output_text.delta\"}\n\n",
            "data-after-response-completed",
        ];
        yield return
        [
            "data: [DONE]\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\"}\n\n",
            "done-before-response-completed",
        ];
        yield return
        [
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\"}\n\n" +
            "data: [DONE]\n\n" +
            ": forbidden after done\n\n",
            "content-after-done",
        ];
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
        string parent = Path.GetDirectoryName(path)!;
        string leaf = Path.GetFileName(path);
        foreach (string artifact in Directory.EnumerateFileSystemEntries(parent, leaf + "*"))
        {
            if (Directory.Exists(artifact))
            {
                Directory.Delete(artifact, recursive: true);
            }
            else
            {
                File.Delete(artifact);
            }
        }
    }

    private static async Task<string> ReadEvidenceAsync(string outputDirectory)
        => await File.ReadAllTextAsync(Path.Combine(outputDirectory, WireCapture.SseFileName))
            + await File.ReadAllTextAsync(Path.Combine(outputDirectory, WireCapture.EventsFileName));

    private static async Task<string> ReadFailedEvidenceAsync(string outputDirectory)
        => await File.ReadAllTextAsync(
                Path.Combine(FailedBundleDirectory(outputDirectory), WireCapture.FailedSseFileName))
            + await File.ReadAllTextAsync(
                Path.Combine(FailedBundleDirectory(outputDirectory), WireCapture.FailedEventsFileName));

    private static string FailedBundleDirectory(string outputDirectory)
        => outputDirectory + ".failed";

    private static async Task SeedStaleSuccessFilesAsync(string outputDirectory)
    {
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, WireCapture.SseFileName),
            "stale-success");
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, WireCapture.EventsFileName),
            "stale-success");
    }

    private static IEnumerable<string> EnumerateSiblingArtifacts(
        string outputDirectory,
        string infix)
    {
        string parent = Path.GetDirectoryName(outputDirectory)!;
        string prefix = Path.GetFileName(outputDirectory) + infix;
        return Directory.EnumerateDirectories(parent)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal));
    }

    private static async Task MarkOwnedArtifactAsync(string artifactPath)
        => await File.WriteAllTextAsync(
            OwnedArtifactMarker(artifactPath),
            OwnedArtifactSignature + Environment.NewLine + Path.GetFullPath(artifactPath));

    private static string OwnedArtifactMarker(string artifactPath)
        => artifactPath + ".owned";

    private static void AssertNoTemporaryFiles(string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
        {
            Assert.DoesNotContain(
                Directory.EnumerateFiles(outputDirectory),
                path => Path.GetFileName(path).Contains(".tmp.", StringComparison.Ordinal));
        }

        string parent = Path.GetDirectoryName(outputDirectory)!;
        string prefix = Path.GetFileName(outputDirectory) + ".";
        Assert.DoesNotContain(
            Directory.EnumerateFiles(parent, prefix + "*.owned"),
            marker => File.ReadAllText(marker)
                .StartsWith(OwnedArtifactSignature + Environment.NewLine, StringComparison.Ordinal));
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        return count;
    }

    private static async Task<(int ExitCode, string Stderr)> CaptureThroughApplicationAsync(
        Uri url,
        string outputDirectory)
    {
        StringWriter error = new();
        int exitCode = await ProbeApplication.RunAsync(
            ["capture-wire", "--url", url.AbsoluteUri, "--output", outputDirectory],
            TextWriter.Null,
            error,
            (_, _) => throw new InvalidOperationException("probe must not run"),
            (_, _, _) => throw new InvalidOperationException("serve must not run"),
            (command, cancellationToken) => WireCapture.CaptureAsync(
                command.Url,
                command.OutputDirectory,
                cancellationToken));
        return (exitCode, error.ToString());
    }

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
