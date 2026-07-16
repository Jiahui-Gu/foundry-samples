// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;

namespace BrowserAutomation;

/// <summary>
/// Stateless playwright-cli session runner. Each tool call creates a new instance
/// with the sessionId — no server-side state is tracked. The model manages session
/// lifecycle (cdpUrl, sessionId) in its conversation context.
/// </summary>
public class BrowserSession
{
    public string SessionId { get; }

    private readonly int _timeoutSeconds;
    private readonly ILogger? _logger;

    public BrowserSession(string sessionId, int timeoutSeconds = 180, ILogger? logger = null)
    {
        SessionId = sessionId;
        _timeoutSeconds = timeoutSeconds;
        _logger = logger;
    }

    /// <summary>
    /// Run a playwright-cli command. If cdpUrl is provided, sets PLAYWRIGHT_MCP_CDP_ENDPOINT
    /// for the subprocess (used on first 'open about:blank' to connect).
    /// </summary>
    public async Task<string> RunCommandAsync(string command, string? cdpUrl = null)
    {
        var cli = FindCli();
        var executable = cli;
        var argumentPrefix = string.Empty;

        if (OperatingSystem.IsWindows() && cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var cliScript = Path.Combine(
                Path.GetDirectoryName(cli)!,
                "node_modules",
                "@playwright",
                "cli",
                "playwright-cli.js");
            if (!File.Exists(cliScript))
            {
                _logger?.LogError("[pw-cli] Could not locate the JavaScript entry point next to {Cli}", cli);
                return $"Error: Could not locate the playwright-cli entry point next to '{cli}'.";
            }

            var bundledNode = Path.Combine(Path.GetDirectoryName(cli)!, "node.exe");
            executable = File.Exists(bundledNode) ? bundledNode : "node";
            argumentPrefix = $"\"{cliScript}\" ";
        }

        var args = $"{argumentPrefix}-s={SessionId} {command}";
        _logger?.LogInformation("[pw-cli] {Cli} -s={SessionId} {Command}", executable, SessionId, Redaction.Redact(command));

        ProcessStartInfo psi = new()
        {
            FileName = executable,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(cdpUrl))
            psi.Environment["PLAYWRIGHT_MCP_CDP_ENDPOINT"] = cdpUrl;

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
                _logger?.LogWarning("[pw-cli] Command timed out after {Timeout}s: {Command}", _timeoutSeconds, Redaction.Redact(command));
                return $"Command timed out after {_timeoutSeconds} seconds.";
            }

            var stdout = Redaction.Redact(Truncate(await stdoutTask));
            var stderr = Redaction.Redact(Truncate(await stderrTask));

            _logger?.LogInformation("[pw-cli] exit_code={ExitCode} stdout_len={StdoutLen} stderr_len={StderrLen}",
                process.ExitCode, stdout.Length, stderr.Length);

            return $"exit_code: {process.ExitCode}\nstdout:\n{(string.IsNullOrEmpty(stdout) ? "<empty>" : stdout)}\n\nstderr:\n{(string.IsNullOrEmpty(stderr) ? "<empty>" : stderr)}";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[pw-cli] Failed to run command");
            return $"Error: Failed to run playwright-cli: {ex.Message}";
        }
    }

    /// <summary>
    /// Close a browser session: detach playwright-cli, then close remote browser via CDP websocket.
    /// Matches the Python MAF pattern: detach + Browser.close over CDP.
    /// </summary>
    public async Task<string> CloseAsync(string cdpUrl)
    {
        // 1. Detach local playwright-cli session
        var detachResult = await RunCommandAsync("detach");

        return $"Session '{SessionId}' closed.\ndetach: {detachResult}";
    }
    
    private static string FindCli()
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "playwright-cli");
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(candidate + ".exe")) return candidate + ".exe";
                if (File.Exists(candidate + ".cmd")) return candidate + ".cmd";
            }
            else if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return "playwright-cli";
    }

    private static string Truncate(string text, int maxLen = 12000) =>
        text.Length <= maxLen ? text : text[..maxLen] + "\n...[truncated]";
}
