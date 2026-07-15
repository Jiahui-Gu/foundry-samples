using DotNetEnv;
using Microsoft.Extensions.AI;

namespace HarnessAgentDiagnostics;

public static class Program
{
    public static Task<int> Main(string[] args)
        => ProbeApplication.RunAsync(
            args,
            Console.Out,
            Console.Error,
            ProbeApplication.RunProductionProbeAsync,
            ProbeApplication.RunProductionServeAsync,
            ProbeApplication.RunProductionWireCaptureAsync);
}

internal static class ProbeApplication
{
    private const string ActivitySourceName = "HarnessAgentDiagnostics.DirectProbe";

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<ProbeCommand, CancellationToken, Task<ProbeRunSummary>> runProbeAsync,
        Func<ServeCommand, TextWriter, CancellationToken, Task>? runServeAsync = null,
        Func<CaptureWireCommand, CancellationToken, Task<WireCaptureSummary>>? runCaptureWireAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(runProbeAsync);

        DiagnosticCommand command;
        try
        {
            command = ProbeCommandParser.Parse(args);
        }
        catch (ProbeCommandException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }

        try
        {
            switch (command)
            {
                case ProbeCommand probe:
                    ProbeRunSummary probeSummary = await runProbeAsync(probe, cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"Probe complete: updates={probeSummary.UpdateCount} contents={probeSummary.ContentCount} activities={probeSummary.ActivityNames.Count} output={probe.OutputDirectory}")
                        .ConfigureAwait(false);
                    break;
                case ServeCommand serve:
                    if (runServeAsync is null)
                    {
                        throw new InvalidOperationException("Serve command is unavailable.");
                    }

                    await runServeAsync(serve, output, cancellationToken).ConfigureAwait(false);
                    break;
                case CaptureWireCommand capture:
                    if (runCaptureWireAsync is null)
                    {
                        throw new InvalidOperationException("Capture command is unavailable.");
                    }

                    WireCaptureSummary wireSummary = await runCaptureWireAsync(capture, cancellationToken)
                        .ConfigureAwait(false);
                    if (!wireSummary.FailureMarkers.IsEmpty || !wireSummary.MissingMarkers.IsEmpty)
                    {
                        string markers = string.Join(
                            ",",
                            wireSummary.FailureMarkers.Concat(wireSummary.MissingMarkers));
                        await error.WriteLineAsync($"Wire capture failed: markers={markers}")
                            .ConfigureAwait(false);
                        return 1;
                    }

                    await output.WriteLineAsync(
                        $"Wire capture complete: events={wireSummary.EventCount} done={wireSummary.Done} completed={wireSummary.Completed} output={capture.OutputDirectory}")
                        .ConfigureAwait(false);
                    break;
            }

            return 0;
        }
        catch (InvalidOperationException exception) when (IsSafeConfigurationError(exception.Message))
        {
            await error.WriteLineAsync($"Configuration error: {exception.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (WireCaptureException exception)
        {
            await error.WriteLineAsync($"Wire capture failed: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Probe failed: {exception.GetType().Name}.").ConfigureAwait(false);
            return 1;
        }
    }

    private static bool IsSafeConfigurationError(string message)
        => message is
            "Missing or invalid required environment variable: FOUNDRY_PROJECT_ENDPOINT."
            or "Missing or invalid required environment variable: AZURE_AI_MODEL_DEPLOYMENT_NAME.";

    internal static async Task<ProbeRunSummary> RunProductionProbeAsync(
        ProbeCommand command,
        CancellationToken cancellationToken)
    {
        LoadNearbyEnvironment(AppContext.BaseDirectory);
        ProbeConfiguration configuration = ProbeConfiguration.FromEnvironment();
        using IChatClient chatClient = ProbeAgentFactory.CreateFoundryChatClient(configuration);
        ProbeAgentContext context = ProbeAgentFactory.Create(chatClient, ActivitySourceName);
        string[] sensitiveValues =
        [
            configuration.ProjectEndpoint.AbsoluteUri,
            configuration.ModelDeployment,
        ];

        await using DiagnosticRecorder recorder =
            new(command.OutputDirectory, sensitiveValues);
        DirectProbe probe = new(context, recorder);
        return await probe.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RunProductionServeAsync(
        ServeCommand command,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        LoadNearbyEnvironment(AppContext.BaseDirectory);
        ProbeConfiguration configuration = ProbeConfiguration.FromEnvironment();
        using IChatClient chatClient = ProbeAgentFactory.CreateFoundryChatClient(configuration);
        ProbeAgentContext context = ProbeAgentFactory.Create(chatClient, ActivitySourceName);
        await HostedProbe.RunAsync(context.Agent, command.Url, output, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static Task<WireCaptureSummary> RunProductionWireCaptureAsync(
        CaptureWireCommand command,
        CancellationToken cancellationToken)
        => WireCapture.CaptureAsync(command.Url, command.OutputDirectory, cancellationToken);

    internal static void LoadNearbyEnvironment(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        for (DirectoryInfo? directory = new(baseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, ".env");
            if (File.Exists(path))
            {
                Env.NoClobber().Load(path);
                return;
            }
        }
    }
}
