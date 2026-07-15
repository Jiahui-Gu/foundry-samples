// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;

namespace BrowserAutomation;

/// <summary>
/// Manages a playwright-cli session against a remote CDP browser.
/// </summary>
public class BrowserSession
{
    public string SessionId { get; }
    public bool Connected { get; private set; }

    private readonly int _timeoutSeconds;
    private readonly ILogger? _logger;

    public BrowserSession(string sessionId, int timeoutSeconds = 180, ILogger? logger = null)
    {
        SessionId = sessionId;
        _timeoutSeconds = timeoutSeconds;
        _logger = logger;
    }

    /// <summary>Attach to a remote browser via CDP URL.</summary>
    public async Task<(bool Success, string Output)> ConnectAsync(string cdpUrl)
    {
        var result = await RunCommandAsync("attach", $"--cdp={cdpUrl}");
        Connected = result.Success;
        return result;
    }

    /// <summary>Run a playwright-cli command in this session.</summary>
    public async Task<(bool Success, string Output)> RunAsync(string command, string[] args)
    {
        if (!Connected)
            return (false, "Browser not connected. Session may need to be recreated.");

        return await RunCommandAsync([command, .. args]);
    }

    /// <summary>Detach from the browser session.</summary>
    public async Task CloseAsync()
    {
        if (Connected)
            await RunCommandAsync("detach");
        Connected = false;
    }

    private async Task<(bool Success, string Output)> RunCommandAsync(params string[] arguments)
    {
        var cli = FindCli();
        var command = string.Join(" ", arguments.Select(FormatArgument));
        _logger?.LogInformation("[pw-cli] {Cli} -s={SessionId} {Command}", cli.DisplayName, SessionId, Redaction.Redact(command));

        ProcessStartInfo psi = new()
        {
            FileName = cli.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var prefixArgument in cli.PrefixArguments)
            psi.ArgumentList.Add(prefixArgument);
        psi.ArgumentList.Add($"-s={SessionId}");
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start playwright-cli");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (false, $"Command timed out after {_timeoutSeconds} seconds.");
            }

            var stdout = Redaction.Redact(Truncate(await stdoutTask));
            var stderr = Redaction.Redact(Truncate(await stderrTask));
            var success = process.ExitCode == 0;

            var output = $"exit_code: {process.ExitCode}\nstdout:\n{(string.IsNullOrEmpty(stdout) ? "<empty>" : stdout)}";
            if (!string.IsNullOrEmpty(stderr))
                output += $"\n\nstderr:\n{stderr}";

            return (success, output);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[pw-cli] Failed to run command");
            return (false, $"Error: Failed to run playwright-cli: {ex.Message}");
        }
    }

    private static CliLaunch FindCli()
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            var candidate = Path.Combine(dir, "playwright-cli");
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(candidate + ".exe"))
                    return new(candidate + ".exe", candidate + ".exe", []);

                // npm's extensionless and .cmd launchers cannot be executed safely
                // with redirected streams. Invoke the installed JavaScript entry
                // point directly so signed CDP URLs remain one argument.
                var script = Path.Combine(dir, "node_modules", "@playwright", "cli", "playwright-cli.js");
                if (File.Exists(script))
                {
                    var node = FindOnPath("node.exe")
                        ?? throw new FileNotFoundException(
                            "node.exe was not found on PATH. Install Node.js before using playwright-cli.");
                    return new(node, candidate, [script]);
                }
            }
            else if (File.Exists(candidate))
            {
                return new(candidate, candidate, []);
            }
        }

        throw new FileNotFoundException(
            "playwright-cli was not found on PATH. Install it with 'npm install -g @playwright/cli@latest'.");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string FormatArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

    private sealed record CliLaunch(
        string FileName,
        string DisplayName,
        IReadOnlyList<string> PrefixArguments);

    private static string Truncate(string text, int maxLen = 12000) =>
        text.Length <= maxLen ? text : text[..maxLen] + "\n...[truncated]";
}
