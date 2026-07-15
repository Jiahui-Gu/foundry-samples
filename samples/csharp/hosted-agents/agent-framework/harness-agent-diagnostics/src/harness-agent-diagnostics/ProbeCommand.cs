namespace HarnessAgentDiagnostics;

internal sealed record ProbeCommand(string OutputDirectory);

internal sealed class ProbeCommandException(string message) : Exception(message);

internal static class ProbeCommandParser
{
    private const string Usage = "Usage: harness-agent-diagnostics probe [--output <directory>]";

    internal static ProbeCommand Parse(string[] arguments, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Length == 0 || !string.Equals(arguments[0], "probe", StringComparison.Ordinal))
        {
            throw new ProbeCommandException(Usage);
        }

        string outputDirectory = Path.Combine(
            baseDirectory ?? AppContext.BaseDirectory,
            "run-output",
            "direct");

        bool outputSeen = false;
        for (int index = 1; index < arguments.Length; index++)
        {
            if (!string.Equals(arguments[index], "--output", StringComparison.Ordinal)
                || outputSeen
                || index + 1 >= arguments.Length
                || string.IsNullOrWhiteSpace(arguments[index + 1])
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ProbeCommandException(Usage);
            }

            outputSeen = true;
            outputDirectory = arguments[++index];
        }

        return new ProbeCommand(outputDirectory);
    }
}
