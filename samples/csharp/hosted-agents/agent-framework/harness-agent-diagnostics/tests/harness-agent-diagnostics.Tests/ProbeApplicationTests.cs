using System.Collections.Immutable;
using System.Net;
using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public sealed class ProbeApplicationTests
{
    [Fact]
    public async Task RunAsync_RejectsUnknownCommandWithoutInvokingProbe()
    {
        StringWriter output = new();
        StringWriter error = new();
        bool invoked = false;

        int exitCode = await ProbeApplication.RunAsync(
            ["future-command"],
            output,
            error,
            (_, _) =>
            {
                invoked = true;
                throw new InvalidOperationException();
            });

        Assert.NotEqual(0, exitCode);
        Assert.False(invoked);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReportsOnlyShortCountsAndExplicitOutputPath()
    {
        StringWriter output = new();
        StringWriter error = new();
        ProbeCommand? received = null;

        int exitCode = await ProbeApplication.RunAsync(
            ["probe", "--output", "explicit-output"],
            output,
            error,
            (command, _) =>
            {
                received = command;
                return Task.FromResult(new ProbeRunSummary(
                    2,
                    3,
                    ["TextContent"],
                    "execute",
                    [],
                    [],
                    ["probe.activity"],
                    driverFallbackUsed: false,
                    recoveryTurnUsed: false,
                    [],
                    []));
            });

        Assert.Equal(0, exitCode);
        Assert.Equal("explicit-output", received!.OutputDirectory);
        Assert.Empty(error.ToString());
        Assert.Contains("updates=2", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("contents=3", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("activities=1", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("explicit-output", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PrintsConfigurationErrorButNeverRuntimeExceptionMessage()
    {
        StringWriter configurationError = new();
        int configurationExitCode = await ProbeApplication.RunAsync(
            ["probe"],
            TextWriter.Null,
            configurationError,
            (_, _) => throw new InvalidOperationException(
                "Missing or invalid required environment variable: FOUNDRY_PROJECT_ENDPOINT."));

        const string secret = "TOP-SECRET-APPLICATION";
        StringWriter runtimeError = new();
        int runtimeExitCode = await ProbeApplication.RunAsync(
            ["probe"],
            TextWriter.Null,
            runtimeError,
            (_, _) => throw new InvalidOperationException(secret));

        Assert.NotEqual(0, configurationExitCode);
        Assert.Contains("FOUNDRY_PROJECT_ENDPOINT", configurationError.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(0, runtimeExitCode);
        Assert.Contains("InvalidOperationException", runtimeError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, runtimeError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DispatchesServeWithoutInvokingProbeOrCapture()
    {
        StringWriter output = new();
        ServeCommand? received = null;

        int exitCode = await ProbeApplication.RunAsync(
            ["serve", "--url", "http://localhost:9010"],
            output,
            TextWriter.Null,
            (_, _) => throw new InvalidOperationException("probe must not run"),
            (command, _, _) =>
            {
                received = command;
                return Task.CompletedTask;
            },
            (_, _) => throw new InvalidOperationException("capture must not run"));

        Assert.Equal(0, exitCode);
        Assert.Equal(new Uri("http://localhost:9010"), received!.Url);
    }

    [Fact]
    public async Task RunAsync_DispatchesCredentialFreeCaptureWithoutLoadingConfiguration()
    {
        StringWriter output = new();
        CaptureWireCommand? received = null;

        int exitCode = await ProbeApplication.RunAsync(
            ["capture-wire", "--url", "http://127.0.0.1:9011", "--output", "wire"],
            output,
            TextWriter.Null,
            (_, _) => throw new InvalidOperationException("probe must not run"),
            (_, _, _) => throw new InvalidOperationException("serve must not run"),
            (command, _) =>
            {
                received = command;
                return Task.FromResult(new WireCaptureSummary(
                    HttpStatusCode.OK,
                    "text/event-stream",
                    2,
                    ImmutableArray.Create("response.completed", "message"),
                    ImmutableArray.Create("response.completed"),
                    Done: true,
                    Completed: true,
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty));
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(new Uri("http://127.0.0.1:9011"), received!.Url);
        Assert.Equal("wire", received.OutputDirectory);
        Assert.Contains("events=2", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("wire", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureForIncompleteWireCapture()
    {
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await ProbeApplication.RunAsync(
            ["capture-wire"],
            output,
            error,
            (_, _) => throw new InvalidOperationException("probe must not run"),
            (_, _, _) => throw new InvalidOperationException("serve must not run"),
            (_, _) => Task.FromResult(new WireCaptureSummary(
                HttpStatusCode.OK,
                "text/event-stream",
                1,
                ImmutableArray.Create("response.delta"),
                ImmutableArray.Create("response.delta"),
                Done: false,
                Completed: false,
                ImmutableArray.Create("malformed-json"),
                ImmutableArray.Create("missing-response-completed", "missing-done"))));

        Assert.NotEqual(0, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("malformed-json", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("missing-response-completed", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("complete:", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
