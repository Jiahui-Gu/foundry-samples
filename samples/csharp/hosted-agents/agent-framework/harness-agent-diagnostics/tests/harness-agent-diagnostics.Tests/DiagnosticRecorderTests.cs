using System.Collections;
using System.Reflection;
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
    public async Task RecordProviderStateAsync_LeavesCustomEnumerablesOpaqueWithoutInvokingEnumerator()
    {
        string outputDirectory = CreateOutputDirectory();
        ThrowingEnumerable values = new();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(new ProviderStateWithCustomEnumerable(values));
            }

            Assert.False(values.EnumeratorInvoked);

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl")));
            Assert.Equal(
                typeof(ThrowingEnumerable).FullName,
                document.RootElement.GetProperty("Values").GetProperty("type").GetString());
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
    public async Task RecordProviderStateAsync_RemovesUnsafeJsonNodeSubtreesByKey()
    {
        string outputDirectory = CreateOutputDirectory();
        JsonObject providerState = new()
        {
            ["rawRepresentationType"] = "Provider.SafeEnvelope",
            ["visible"] = "safe-value",
            ["nested"] = new JsonObject
            {
                ["rawRepresentationType"] = new JsonObject { ["payload"] = "disguised-raw-payload-secret" },
                ["rawRepresentation"] = new JsonObject { ["payload"] = "raw-payload-secret" },
                ["credentialState"] = new JsonObject { ["value"] = "credential-payload-secret" },
                ["accessToken"] = new JsonObject { ["value"] = "token-payload-secret" },
                ["requestHeaders"] = new JsonObject { ["value"] = "header-payload-secret" },
                ["exceptionDetails"] = new JsonObject { ["message"] = "exception-payload-secret" },
                ["stackFrames"] = new JsonArray("stack-payload-secret"),
                ["transport"] = new JsonObject { ["body"] = "transport-payload-secret" },
            },
        };

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(providerState);
            }

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl")));
            JsonElement root = document.RootElement;
            JsonElement nested = root.GetProperty("nested");

            Assert.Equal("Provider.SafeEnvelope", root.GetProperty("rawRepresentationType").GetString());
            Assert.Equal("safe-value", root.GetProperty("visible").GetString());
            Assert.False(nested.TryGetProperty("rawRepresentationType", out _));
            Assert.False(nested.TryGetProperty("rawRepresentation", out _));
            Assert.False(nested.TryGetProperty("credentialState", out _));
            Assert.False(nested.TryGetProperty("accessToken", out _));
            Assert.False(nested.TryGetProperty("requestHeaders", out _));
            Assert.False(nested.TryGetProperty("exceptionDetails", out _));
            Assert.False(nested.TryGetProperty("stackFrames", out _));
            Assert.False(nested.TryGetProperty("transport", out _));
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
    public async Task RecordProviderStateAsync_AliasesAlphabeticToolIdentifiersInCollectionsAndText()
    {
        const string toolId = "tool_abcdefghijklmnop";
        const string ordinaryWord = "tool_assistance";
        const string eventName = "response.tool.completed";
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(new JsonObject
                {
                    ["values"] = new JsonArray(toolId, ordinaryWord, eventName),
                    ["baggage"] = $"selected={toolId}; helper={ordinaryWord}; event={eventName}",
                });
            }

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl")));
            JsonElement root = document.RootElement;
            JsonElement values = root.GetProperty("values");
            string alias = values[0].GetString()!;

            Assert.StartsWith("tool-", alias, StringComparison.Ordinal);
            Assert.Equal(ordinaryWord, values[1].GetString());
            Assert.Equal(eventName, values[2].GetString());
            Assert.DoesNotContain(toolId, root.GetRawText(), StringComparison.Ordinal);
            Assert.Contains($"selected={alias}", root.GetProperty("baggage").GetString(), StringComparison.Ordinal);
            Assert.Contains($"helper={ordinaryWord}", root.GetProperty("baggage").GetString(), StringComparison.Ordinal);
            Assert.Contains($"event={eventName}", root.GetProperty("baggage").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordProviderStateAsync_RedactsAzureResourceScopesWithinOrdinaryText()
    {
        const string subscriptionId = "/subscriptions/11111111-2222-3333-4444-555555555555";
        const string resourceGroupId = "/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-harness";
        const string providerResourceId = "/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-harness/providers/Microsoft.KeyVault/vaults/harness-kv/secrets/probe/versions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string prefix = "Ordinary text before ";
        const string suffix = " and after the ids.";
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordProviderStateAsync(new
                {
                    subscriptionId,
                    resourceGroupId,
                    providerResourceId,
                    repeatedProviderResourceId = providerResourceId,
                    message = $"{prefix}{subscriptionId}, {resourceGroupId}, and {providerResourceId}{suffix}",
                });
            }

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "provider-state.jsonl")));
            JsonElement root = document.RootElement;
            string json = root.GetRawText();

            Assert.DoesNotContain(subscriptionId, json, StringComparison.Ordinal);
            Assert.DoesNotContain(resourceGroupId, json, StringComparison.Ordinal);
            Assert.DoesNotContain(providerResourceId, json, StringComparison.Ordinal);
            Assert.StartsWith("azure-resource-", root.GetProperty("subscriptionId").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("azure-resource-", root.GetProperty("resourceGroupId").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("azure-resource-", root.GetProperty("providerResourceId").GetString(), StringComparison.Ordinal);
            Assert.Equal(
                root.GetProperty("providerResourceId").GetString(),
                root.GetProperty("repeatedProviderResourceId").GetString());

            string message = root.GetProperty("message").GetString()!;
            Assert.Contains(prefix, message, StringComparison.Ordinal);
            Assert.Contains(suffix, message, StringComparison.Ordinal);
            Assert.DoesNotContain(subscriptionId, message, StringComparison.Ordinal);
            Assert.DoesNotContain(resourceGroupId, message, StringComparison.Ordinal);
            Assert.DoesNotContain(providerResourceId, message, StringComparison.Ordinal);
            Assert.Equal(
                $"{prefix}{root.GetProperty("subscriptionId").GetString()}, {root.GetProperty("resourceGroupId").GetString()}, and {root.GetProperty("providerResourceId").GetString()}{suffix}",
                message);
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
                        ["authorization"] = "plain-auth-token",
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
    public async Task RecordAgentResponseUpdateAsync_PersistsSanitizedContinuationTokenAndUsageTokenCounts()
    {
        string outputDirectory = CreateOutputDirectory();
        ResponseContinuationToken continuationToken = ResponseContinuationToken.FromBytes("opaque-continuation-token-value"u8.ToArray());
        UsageDetails usageDetails = new();
        IReadOnlyDictionary<string, long> expectedTokenCounts = SetUsageTokenCounts(usageDetails);
        string rawContinuationToken = Convert.ToBase64String(continuationToken.ToBytes().ToArray());

        try
        {
            AgentResponseUpdate update = new(
                ChatRole.Assistant,
                [new UsageContent(usageDetails), new TextContent("visible text")])
            {
                ContinuationToken = continuationToken,
                AdditionalProperties = new AdditionalPropertiesDictionary(new Dictionary<string, object?>
                {
                    ["accessToken"] = "plain-access-token",
                    ["refreshToken"] = "plain-refresh-token",
                    ["authorization"] = "plain-auth-token",
                    ["note"] = "safe note",
                }),
            };

            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                await recorder.RecordAgentResponseUpdateAsync(update);
            }

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(outputDirectory, "agent-response-updates.jsonl")));
            JsonElement root = document.RootElement;
            JsonElement continuation = root.GetProperty("continuationToken");

            Assert.Equal(typeof(ResponseContinuationToken).FullName, continuation.GetProperty("type").GetString());
            Assert.Equal(continuationToken.ToBytes().Length, continuation.GetProperty("byteLength").GetInt32());
            string continuationAlias = continuation.GetProperty("value").GetString()!;
            Assert.StartsWith("continuation-token-", continuationAlias, StringComparison.Ordinal);
            Assert.DoesNotContain(rawContinuationToken, root.GetRawText(), StringComparison.Ordinal);

            JsonElement usage = root.GetProperty("contents")
                .EnumerateArray()
                .Single(content => content.GetProperty("type").GetString() == nameof(UsageContent));
            JsonElement usageDetailsElement = usage.GetProperty("details");
            foreach ((string name, long expectedValue) in expectedTokenCounts)
            {
                Assert.True(
                    usageDetailsElement.TryGetProperty(name, out JsonElement property),
                    $"Expected usage detail '{name}' to be persisted.");
                Assert.Equal(expectedValue, property.GetInt64());
            }

            JsonElement additionalProperties = root.GetProperty("additionalProperties");
            Assert.Equal("[REDACTED]", additionalProperties.GetProperty("accessToken").GetString());
            Assert.Equal("[REDACTED]", additionalProperties.GetProperty("refreshToken").GetString());
            Assert.False(additionalProperties.TryGetProperty("authorization", out _));
            Assert.Equal("safe note", additionalProperties.GetProperty("note").GetString());
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

    [Fact]
    public async Task DisposeAsync_FlushesCallsStartedBeforeDisposalAndRejectsLaterCalls()
    {
        string outputDirectory = CreateOutputDirectory();
        TaskCompletionSource writeAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DiagnosticRecorder recorder = new(
            outputDirectory,
            sensitiveValues: null,
            beforeWriteAsync: _ =>
            {
                writeAccepted.TrySetResult();
                return releaseWrite.Task;
            });

        try
        {
            Task acceptedRecord = recorder.RecordProviderStateAsync(new { value = "accepted-before-dispose" });
            await writeAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            ValueTask disposal = recorder.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => recorder.RecordProviderStateAsync(new { value = "started-after-dispose" }));

            releaseWrite.TrySetResult();
            await acceptedRecord;
            await disposal;

            string[] lines = await File.ReadAllLinesAsync(Path.Combine(outputDirectory, "provider-state.jsonl"));
            Assert.Single(lines);
            Assert.Contains("accepted-before-dispose", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            releaseWrite.TrySetResult();
            await recorder.DisposeAsync();
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordProviderStateAsync_ThrowsObjectDisposedExceptionBeforeCancellationForDisposedRecorder()
    {
        string outputDirectory = CreateOutputDirectory();
        DiagnosticRecorder recorder = new(outputDirectory);
        using CancellationTokenSource cts = new();

        try
        {
            await recorder.DisposeAsync();
            await recorder.DisposeAsync();
            cts.Cancel();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => recorder.RecordProviderStateAsync(new { value = "after-dispose" }, cts.Token));
        }
        finally
        {
            await recorder.DisposeAsync();
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RecordProviderStateAsync_PreservesCancellationForLiveRecorder()
    {
        string outputDirectory = CreateOutputDirectory();

        try
        {
            await using var recorder = new DiagnosticRecorder(outputDirectory);
            using CancellationTokenSource cts = new();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => recorder.RecordProviderStateAsync(new { value = "canceled-while-live" }, cts.Token));

            Assert.False(File.Exists(Path.Combine(outputDirectory, "provider-state.jsonl")));
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

    private static IReadOnlyDictionary<string, long> SetUsageTokenCounts(UsageDetails details)
    {
        Dictionary<string, long> expected = new(StringComparer.Ordinal);
        long next = 1;
        foreach (PropertyInfo property in typeof(UsageDetails).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanWrite && property.Name.EndsWith("TokenCount", StringComparison.Ordinal)))
        {
            object value = property.PropertyType switch
            {
                _ when property.PropertyType == typeof(int) => checked((int)next),
                _ when property.PropertyType == typeof(long) => next,
                _ when property.PropertyType == typeof(int?) => checked((int)next),
                _ when property.PropertyType == typeof(long?) => next,
                _ => throw new NotSupportedException($"Unsupported usage token count type {property.PropertyType.FullName}."),
            };

            property.SetValue(details, value);
            expected.Add(ToCamelCase(property.Name), next);
            next++;
        }

        Assert.NotEmpty(expected);
        return expected;
    }

    private static string ToCamelCase(string value)
        => string.IsNullOrEmpty(value)
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

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

    private sealed class ProviderStateWithCustomEnumerable(ThrowingEnumerable values)
    {
        public ThrowingEnumerable Values { get; } = values;
    }

    private sealed class ThrowingEnumerable : IEnumerable<string>
    {
        public bool EnumeratorInvoked { get; private set; }

        public IEnumerator<string> GetEnumerator()
        {
            EnumeratorInvoked = true;
            throw new InvalidOperationException("must not enumerate custom iterables");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

}
