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
}
