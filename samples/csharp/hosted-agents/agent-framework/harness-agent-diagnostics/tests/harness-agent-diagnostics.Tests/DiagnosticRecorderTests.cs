using System.Text.Json;
using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public sealed class DiagnosticRecorderTests
{
    [Fact]
    public async Task RecordProviderStateAsync_RedactsSensitiveValuesAndAliasesRepeatedIdentifiers()
    {
        const string endpoint = "https://contoso.services.ai.azure.com/api/projects/secret-project";
        const string responseId = "resp_0123456789abcdefghijk";
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory, [endpoint, "secret-project"]))
            {
                await recorder.RecordProviderStateAsync(new
                {
                    endpoint,
                    responseId,
                    repeatedResponseId = responseId,
                    eventName = "response.output_text.delta",
                    text = "ordinary prompt and output text",
                    model = "gpt-4.1-mini",
                });
            }

            string output = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl"));

            Assert.DoesNotContain(endpoint, output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-project", output, StringComparison.Ordinal);
            Assert.DoesNotContain(responseId, output, StringComparison.Ordinal);
            Assert.Contains("response.output_text.delta", output, StringComparison.Ordinal);
            Assert.Contains("ordinary prompt and output text", output, StringComparison.Ordinal);
            Assert.Contains("gpt-4.1-mini", output, StringComparison.Ordinal);

            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            Assert.Equal(root.GetProperty("responseId").GetString(), root.GetProperty("repeatedResponseId").GetString());
            Assert.StartsWith("response-", root.GetProperty("responseId").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordMethods_WriteCompactParseableJsonlInCallOrder()
    {
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(new { sequence = 1 });
                await recorder.RecordProviderStateAsync(new { sequence = 2 });
            }

            string[] lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "provider-state.jsonl"));

            Assert.Equal(2, lines.Length);
            Assert.All(lines, line => Assert.DoesNotContain(Environment.NewLine, line, StringComparison.Ordinal));
            Assert.Equal(1, JsonDocument.Parse(lines[0]).RootElement.GetProperty("sequence").GetInt32());
            Assert.Equal(2, JsonDocument.Parse(lines[1]).RootElement.GetProperty("sequence").GetInt32());
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordProviderStateAsync_DoesNotSerializeHeadersCredentialsOrExceptions()
    {
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(new
                {
                    authorization = "Bearer test-token",
                    headers = new Dictionary<string, string> { ["x-test"] = "hidden" },
                    requestHeaders = new Dictionary<string, string> { ["x-test"] = "header-secret" },
                    resourceId = "/subscriptions/11111111-2222-3333-4444-555555555555/providers/Microsoft.Authorization/roleAssignments/assignment",
                    credential = new OpaqueCredential("credential-secret"),
                    exception = new InvalidOperationException("exception-secret"),
                });
            }

            string output = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl"));

            Assert.DoesNotContain("test-token", output, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", output, StringComparison.Ordinal);
            Assert.DoesNotContain("header-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("/subscriptions/11111111-2222-3333-4444-555555555555", output, StringComparison.Ordinal);
            Assert.DoesNotContain("credential-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    private static string CreateOutputDirectory()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "diagnostic-test-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteOutputDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class OpaqueCredential(string secret)
    {
        public string Secret { get; } = secret;
    }
}
