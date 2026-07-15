namespace HarnessAgentDiagnostics;

internal abstract record DiagnosticCommand;

internal sealed record ProbeCommand(string OutputDirectory) : DiagnosticCommand;

internal sealed record ServeCommand(Uri Url) : DiagnosticCommand;

internal sealed record CaptureWireCommand(Uri Url, string OutputDirectory) : DiagnosticCommand;

internal sealed class ProbeCommandException(string message) : Exception(message);

internal static class ProbeCommandParser
{
    private const string Usage =
        """
        Usage:
          harness-agent-diagnostics probe [--output <directory>]
          harness-agent-diagnostics serve [--url http://127.0.0.1:8088]
          harness-agent-diagnostics capture-wire [--url http://127.0.0.1:8088] [--output <directory>]
        """;

    internal static DiagnosticCommand Parse(string[] arguments, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Length == 0)
        {
            throw new ProbeCommandException(Usage);
        }

        return arguments[0] switch
        {
            "probe" => ParseProbe(arguments, baseDirectory),
            "serve" => ParseServe(arguments),
            "capture-wire" => ParseCaptureWire(arguments, baseDirectory),
            _ => throw new ProbeCommandException(Usage),
        };
    }

    private static ProbeCommand ParseProbe(string[] arguments, string? baseDirectory)
    {
        string outputDirectory = DefaultOutput(baseDirectory, "direct");
        ParseOptions(
            arguments,
            (name, value) =>
            {
                if (name != "--output")
                {
                    throw new ProbeCommandException(Usage);
                }

                outputDirectory = value;
            });
        return new ProbeCommand(outputDirectory);
    }

    private static ServeCommand ParseServe(string[] arguments)
    {
        Uri url = LoopbackHttpUrl.Parse(LoopbackHttpUrl.Default);
        ParseOptions(
            arguments,
            (name, value) =>
            {
                if (name != "--url")
                {
                    throw new ProbeCommandException(Usage);
                }

                url = ParseUrl(value);
            });
        return new ServeCommand(url);
    }

    private static CaptureWireCommand ParseCaptureWire(string[] arguments, string? baseDirectory)
    {
        Uri url = LoopbackHttpUrl.Parse(LoopbackHttpUrl.Default);
        string outputDirectory = DefaultOutput(baseDirectory, "wire");
        ParseOptions(
            arguments,
            (name, value) =>
            {
                switch (name)
                {
                    case "--url":
                        url = ParseUrl(value);
                        break;
                    case "--output":
                        outputDirectory = value;
                        break;
                    default:
                        throw new ProbeCommandException(Usage);
                }
            });
        return new CaptureWireCommand(url, outputDirectory);
    }

    private static void ParseOptions(string[] arguments, Action<string, string> apply)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 1; index < arguments.Length; index += 2)
        {
            string name = arguments[index];
            if (!name.StartsWith("--", StringComparison.Ordinal)
                || !seen.Add(name)
                || index + 1 >= arguments.Length
                || string.IsNullOrWhiteSpace(arguments[index + 1])
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ProbeCommandException(Usage);
            }

            apply(name, arguments[index + 1]);
        }
    }

    private static Uri ParseUrl(string value)
    {
        try
        {
            return LoopbackHttpUrl.Parse(value);
        }
        catch (ArgumentException)
        {
            throw new ProbeCommandException(Usage);
        }
    }

    private static string DefaultOutput(string? baseDirectory, string leaf)
        => Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "run-output", leaf);
}
