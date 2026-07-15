using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessAgentDiagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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
    public async Task RecordProviderStateAsync_DoesNotInvokeCustomGetters()
    {
        string outputDirectory = CreateOutputDirectory();
        ThrowingProviderState providerState = new();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(providerState);
            }

            Assert.False(providerState.GetterInvoked);

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl")));
            JsonElement root = document.RootElement;
            Assert.Equal("visible", root.GetProperty("Safe").GetString());
            Assert.Equal(typeof(string).FullName, root.GetProperty("Throws").GetProperty("type").GetString());
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordProviderStateAsync_RecordsAnonymousRecordAndJsonObjectSnapshots()
    {
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(new { kind = "anonymous", value = 1 });
                await recorder.RecordProviderStateAsync(new ProviderStateRecord("record", 2));
                await recorder.RecordProviderStateAsync(new JsonObject { ["kind"] = "json", ["value"] = 3 });
            }

            string[] lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "provider-state.jsonl"));
            Assert.Equal(["anonymous", "record", "json"], lines.Select(ReadProviderKind));
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordProviderStateAsync_AliasesToolIdentifiersWithoutChangingOrdinaryProse()
    {
        const string toolId = "tool_0123456789abcdefghijk";
        const string alphabeticToolId = "tool_abcdefghijklmnop";
        const string prose = "Keep tool_assistance as ordinary prose.";
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(new
                {
                    toolId,
                    repeatedToolId = toolId,
                    alphabeticToolId,
                    text = prose,
                });
            }

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl")));
            JsonElement root = document.RootElement;
            Assert.DoesNotContain(toolId, root.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(root.GetProperty("toolId").GetString(), root.GetProperty("repeatedToolId").GetString());
            Assert.StartsWith("tool-", root.GetProperty("toolId").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(alphabeticToolId, root.GetRawText(), StringComparison.Ordinal);
            Assert.StartsWith("tool-", root.GetProperty("alphabeticToolId").GetString(), StringComparison.Ordinal);
            Assert.Equal(prose, root.GetProperty("text").GetString());
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

    [Fact]
    public async Task RecordProviderStateAsync_RedactsPlainSecretFieldsAndCredentialLookingStrings()
    {
        const string accessToken = "plain-access-token";
        const string apiKey = "plain-api-key";
        const string clientSecret = "plain-client-secret";
        const string credentialLookingValue = "sk-proj-0123456789abcdefghijk";
        const string jwtLookingValue = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJwcm9iZSJ9.signature";
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(new
                {
                    accessToken,
                    refreshToken = "plain-refresh-token",
                    idToken = "plain-id-token",
                    apiKey,
                    clientSecret,
                    credential = "plain-credential",
                    connectionString = "Endpoint=https://example.test;AccountKey=plain-account-key",
                    nested = new Dictionary<string, object?>
                    {
                        ["authorization"] = "plain-authorization",
                        ["password"] = "plain-password",
                    },
                    unlabelledKey = credentialLookingValue,
                    unlabelledJwt = jwtLookingValue,
                    eventName = "response.output_text.delta",
                    text = "ordinary diagnostic text",
                    model = "gpt-4.1-mini",
                });
            }

            string output = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl"));

            foreach (string secret in new[]
            {
                accessToken, apiKey, clientSecret, credentialLookingValue, jwtLookingValue,
                "plain-refresh-token", "plain-id-token", "plain-credential", "plain-account-key", "plain-password",
            })
            {
                Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
            }

            Assert.Contains("response.output_text.delta", output, StringComparison.Ordinal);
            Assert.Contains("ordinary diagnostic text", output, StringComparison.Ordinal);
            Assert.Contains("gpt-4.1-mini", output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordAgentResponseUpdateAsync_OnlyAcceptsProjectedUpdates()
    {
        string outputDirectory = CreateOutputDirectory();

        try
        {
            AgentResponseUpdate update = new(ChatRole.Assistant, [new TextReasoningContent("visible") { ProtectedData = "protected-secret" }]);

            Assert.Null(typeof(DiagnosticRecorder).GetMethod(
                nameof(DiagnosticRecorder.RecordAgentResponseUpdateAsync),
                [typeof(object)]));

            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordAgentResponseUpdateAsync(update);
            }

            string output = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "agent-response-updates.jsonl"));
            Assert.Contains("visible", output, StringComparison.Ordinal);
            Assert.DoesNotContain("protected-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordProviderStateAsync_AssignsContiguousSequenceNumbersToConcurrentAcceptedRecords()
    {
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                Task[] records = Enumerable.Range(0, 32)
                    .Select(value => recorder.RecordProviderStateAsync(new { value }))
                    .ToArray();
                await Task.WhenAll(records);
            }

            string[] lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "provider-state.jsonl"));
            int[] recordSequences = lines
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("recordSequence").GetInt32())
                .Order()
                .ToArray();

            Assert.Equal(Enumerable.Range(1, 32), recordSequences);
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

    private static string? ReadProviderKind(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        return root.GetProperty(root.TryGetProperty("kind", out _) ? "kind" : "Kind").GetString();
    }

    private sealed class OpaqueCredential(string secret)
    {
        public string Secret { get; } = secret;
    }

    private sealed class ThrowingProviderState
    {
        public bool GetterInvoked { get; private set; }

        public string Safe { get; } = "visible";

        public string Throws
        {
            get
            {
                GetterInvoked = true;
                throw new InvalidOperationException("must not be invoked");
            }
        }
    }

    private sealed record ProviderStateRecord(string Kind, int Value);
}
