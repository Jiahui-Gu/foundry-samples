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
        var result = await RunCommandAsync(["attach", $"--cdp={cdpUrl}"]);
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
            await RunCommandAsync(["detach"]);
        Connected = false;
    }

    private async Task<(bool Success, string Output)> RunCommandAsync(IReadOnlyList<string> command)
    {
        var (executable, script) = FindCli();
        var commandText = string.Join(" ", command);
        _logger?.LogInformation(
            "[pw-cli] {Cli} -s={SessionId} {Command}",
            script ?? executable,
            SessionId,
            Redaction.Redact(commandText));

        ProcessStartInfo psi = new()
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (script != null)
            psi.ArgumentList.Add(script);
        psi.ArgumentList.Add($"-s={SessionId}");
        foreach (var argument in command)
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

    private static (string Executable, string? Script) FindCli()
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var executable = Path.Combine(dir, "playwright-cli.exe");
            if (File.Exists(executable))
                return (executable, null);

            if (OperatingSystem.IsWindows())
            {
                var commandShim = Path.Combine(dir, "playwright-cli.cmd");
                var script = Path.Combine(dir, "node_modules", "@playwright", "cli", "playwright-cli.js");
                if (File.Exists(commandShim) && File.Exists(script))
                {
                    var bundledNode = Path.Combine(dir, "node.exe");
                    return (File.Exists(bundledNode) ? bundledNode : "node", script);
                }
            }
            else
            {
                var candidate = Path.Combine(dir, "playwright-cli");
                if (File.Exists(candidate))
                    return (candidate, null);
            }
        }

        return (OperatingSystem.IsWindows() ? "playwright-cli.exe" : "playwright-cli", null);
    }

    private static string Truncate(string text, int maxLen = 12000) =>
        text.Length <= maxLen ? text : text[..maxLen] + "\n...[truncated]";
}
