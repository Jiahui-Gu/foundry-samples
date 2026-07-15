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
            ProbeApplication.RunProductionProbeAsync);
}

internal static class ProbeApplication
{
    private const string ActivitySourceName = "HarnessAgentDiagnostics.DirectProbe";

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<ProbeCommand, CancellationToken, Task<ProbeRunSummary>> runProbeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(runProbeAsync);

        ProbeCommand command;
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
            ProbeRunSummary summary = await runProbeAsync(command, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Probe complete: updates={summary.UpdateCount} contents={summary.ContentCount} activities={summary.ActivityNames.Count} output={command.OutputDirectory}").ConfigureAwait(false);
            return 0;
        }
        catch (InvalidOperationException exception) when (IsSafeConfigurationError(exception.Message))
        {
            await error.WriteLineAsync($"Configuration error: {exception.Message}").ConfigureAwait(false);
            return 2;
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
