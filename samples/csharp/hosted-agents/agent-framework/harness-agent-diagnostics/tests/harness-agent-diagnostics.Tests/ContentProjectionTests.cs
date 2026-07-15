using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using HarnessAgentDiagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MEAI001

namespace HarnessAgentDiagnostics.Tests;

public sealed class ContentProjectionTests
{
    [Fact]
    public void Project_RecordsRepresentativeContentWithoutOpaqueValuesOrExceptions()
    {
        FunctionCallContent functionCall = new(
            "call_0123456789abcdefghijk",
            "compute_probe",
            new Dictionary<string, object?> { ["value"] = 7 })
        {
            Exception = new InvalidOperationException("do not serialize this exception"),
        };

        ToolApprovalRequestContent approvalRequest = new(
            "approval-123",
            new ToolCallContent("tool-call-123"));

        AIContent[] contents =
        [
            new TextContent("visible text"),
            new TextReasoningContent("reasoning text") { ProtectedData = "abc" },
            functionCall,
            new FunctionResultContent("call_0123456789abcdefghijk", new { answer = 42 })
            {
                Exception = new InvalidOperationException("do not serialize this exception"),
            },
            new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 5, TotalTokenCount = 8 }),
            new ErrorContent("visible error") { ErrorCode = "probe-error" },
            new DataContent(new byte[] { 1, 2, 3, 4 }, "application/octet-stream") { Name = "probe.bin" },
            new UriContent(new Uri("https://example.test/probe"), "text/plain"),
            approvalRequest,
            approvalRequest.CreateResponse(approved: true, reason: "approved"),
            new UnknownContent(),
        ];

        JsonElement projection = JsonSerializer.SerializeToElement(ContentProjection.Project(contents));
        string json = projection.GetRawText();

        Assert.Equal(contents.Length, projection.GetProperty("contents").GetArrayLength());
        Assert.Contains("TextContent", json, StringComparison.Ordinal);
        Assert.Contains("reasoning text", json, StringComparison.Ordinal);
        Assert.Contains("\"protectedDataLength\":3", json, StringComparison.Ordinal);
        Assert.Contains("FunctionCallContent", json, StringComparison.Ordinal);
        Assert.Contains("\"inputTokenCount\":3", json, StringComparison.Ordinal);
        Assert.Contains("DataContent", json, StringComparison.Ordinal);
        Assert.Contains("ToolApprovalRequestContent", json, StringComparison.Ordinal);
        Assert.Contains("UnknownContent", json, StringComparison.Ordinal);
        JsonElement contentsArray = projection.GetProperty("contents");
        JsonElement fallbackNumbers = contentsArray[contentsArray.GetArrayLength() - 1].GetProperty("properties").GetProperty("numbers");
        Assert.Equal(101, fallbackNumbers.GetArrayLength());
        Assert.True(fallbackNumbers[fallbackNumbers.GetArrayLength() - 1].GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("do not serialize this exception", json, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ProjectsHostedAndHostedToolContentSafelyInOrder()
    {
        DateTimeOffset createdAt = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        AIContent[] contents =
        [
            new HostedFileContent("file_0123456789")
            {
                MediaType = "text/plain",
                Name = "probe.txt",
                SizeInBytes = 12,
                CreatedAt = createdAt,
                Purpose = "assistants",
                Scope = "probe-scope",
                RawRepresentation = new RawEnvelope(),
            },
            new HostedVectorStoreContent("vs_0123456789")
            {
                RawRepresentation = new RawEnvelope(),
            },
            new McpServerToolCallContent("call_mcp_0123456789", "lookup", "probe-server")
            {
                Arguments = new Dictionary<string, object?> { ["term"] = "safe" },
                RawRepresentation = new RawEnvelope(),
            },
            new McpServerToolResultContent("call_mcp_0123456789")
            {
                Outputs = [new TextContent("mcp-output-1"), new TextContent("mcp-output-2")],
                RawRepresentation = new RawEnvelope(),
            },
            new CodeInterpreterToolCallContent("call_code_0123456789")
            {
                Inputs = [new TextContent("first input"), new TextContent("second input")],
                RawRepresentation = new RawEnvelope(),
            },
            new CodeInterpreterToolResultContent("call_code_0123456789")
            {
                Outputs = [new TextContent("code-output-1"), new TextContent("code-output-2")],
                RawRepresentation = new RawEnvelope(),
            },
            new WebSearchToolCallContent("call_web_0123456789")
            {
                Queries = ["first query", "second query"],
                RawRepresentation = new RawEnvelope(),
            },
            new WebSearchToolResultContent("call_web_0123456789")
            {
                Outputs =
                [
                    new UriContent(new Uri("https://example.test/first"), "text/html"),
                    new UriContent(new Uri("https://example.test/second"), "text/html"),
                ],
                RawRepresentation = new RawEnvelope(),
            },
            new ImageGenerationToolCallContent("call_image_0123456789")
            {
                AdditionalProperties = new AdditionalPropertiesDictionary(
                    new Dictionary<string, object?> { ["prompt"] = "safe prompt" }),
                RawRepresentation = new RawEnvelope(),
            },
            new ImageGenerationToolResultContent("call_image_0123456789")
            {
                Outputs =
                [
                    new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "generated.png" },
                    new UriContent(new Uri("https://example.test/generated.png"), "image/png"),
                ],
                RawRepresentation = new RawEnvelope(),
            },
        ];

        JsonElement projected = JsonSerializer.SerializeToElement(ContentProjection.Project(contents));
        JsonElement items = projected.GetProperty("contents");

        Assert.Equal(contents.Select(content => content.GetType().Name), items.EnumerateArray().Select(item => item.GetProperty("type").GetString()));
        Assert.Equal("file_0123456789", items[0].GetProperty("fileId").GetString());
        Assert.Equal("text/plain", items[0].GetProperty("mediaType").GetString());
        Assert.Equal("probe.txt", items[0].GetProperty("name").GetString());
        Assert.Equal(12, items[0].GetProperty("sizeInBytes").GetInt64());
        Assert.Equal(createdAt, items[0].GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal("assistants", items[0].GetProperty("purpose").GetString());
        Assert.Equal("probe-scope", items[0].GetProperty("scope").GetString());
        Assert.Equal("vs_0123456789", items[1].GetProperty("vectorStoreId").GetString());
        Assert.Equal("call_mcp_0123456789", items[2].GetProperty("callId").GetString());
        Assert.Equal("lookup", items[2].GetProperty("name").GetString());
        Assert.Equal("probe-server", items[2].GetProperty("serverName").GetString());
        Assert.Equal("safe", items[2].GetProperty("arguments").GetProperty("term").GetString());
        Assert.Equal("call_mcp_0123456789", items[3].GetProperty("callId").GetString());
        Assert.Equal(["mcp-output-1", "mcp-output-2"], ProjectedTexts(items[3], "outputs"));
        Assert.Equal("call_code_0123456789", items[4].GetProperty("callId").GetString());
        Assert.Equal(["first input", "second input"], ProjectedTexts(items[4], "inputs"));
        Assert.Equal("call_code_0123456789", items[5].GetProperty("callId").GetString());
        Assert.Equal(["code-output-1", "code-output-2"], ProjectedTexts(items[5], "outputs"));
        Assert.Equal("call_web_0123456789", items[6].GetProperty("callId").GetString());
        Assert.Equal(["first query", "second query"], StringValues(items[6], "queries"));
        Assert.Equal("call_web_0123456789", items[7].GetProperty("callId").GetString());
        Assert.Equal(
            ["https://example.test/first", "https://example.test/second"],
            items[7].GetProperty("outputs").EnumerateArray().Select(output => output.GetProperty("uri").GetString()));
        Assert.Equal("call_image_0123456789", items[8].GetProperty("callId").GetString());
        Assert.Equal("safe prompt", items[8].GetProperty("additionalProperties").GetProperty("prompt").GetString());
        Assert.Equal("call_image_0123456789", items[9].GetProperty("callId").GetString());
        Assert.Equal("generated.png", items[9].GetProperty("outputs")[0].GetProperty("name").GetString());
        Assert.Equal(3, items[9].GetProperty("outputs")[0].GetProperty("dataLength").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[9].GetProperty("outputs")[0].GetProperty("uri").ValueKind);
        Assert.Equal("https://example.test/generated.png", items[9].GetProperty("outputs")[1].GetProperty("uri").GetString());
        Assert.All(items.EnumerateArray(), item => Assert.Equal(typeof(RawEnvelope).FullName, item.GetProperty("rawRepresentationType").GetString()));
        Assert.DoesNotContain("AQID", projected.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Project_AgentResponseUpdate_ProjectsEveryEnvelopeFieldAndOrderedContents()
    {
        DateTimeOffset createdAt = new(2026, 7, 15, 10, 30, 0, TimeSpan.Zero);
        AgentResponseUpdate update = new(ChatRole.Assistant, [new TextContent("first"), new TextContent("second")])
        {
            AuthorName = "probe-author",
            AgentId = "agent-123",
            ResponseId = "resp_0123456789abcdefghijk",
            MessageId = "msg_0123456789abcdefghijk",
            CreatedAt = createdAt,
            FinishReason = ChatFinishReason.Stop,
            ContinuationToken = ResponseContinuationToken.FromBytes("continuation-token"u8.ToArray()),
            AdditionalProperties = new AdditionalPropertiesDictionary(new Dictionary<string, object?> { ["probe"] = true }),
            RawRepresentation = new RawEnvelope(),
        };

        JsonElement projection = JsonSerializer.SerializeToElement(ContentProjection.Project(update));

        Assert.Equal(update.Role?.ToString(), projection.GetProperty("role").GetString());
        Assert.Equal("probe-author", projection.GetProperty("authorName").GetString());
        Assert.Equal("agent-123", projection.GetProperty("agentId").GetString());
        Assert.Equal("resp_0123456789abcdefghijk", projection.GetProperty("responseId").GetString());
        Assert.Equal("msg_0123456789abcdefghijk", projection.GetProperty("messageId").GetString());
        Assert.Equal(createdAt, projection.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(update.FinishReason?.ToString(), projection.GetProperty("finishReason").GetString());
        Assert.Equal(typeof(ResponseContinuationToken).FullName, projection.GetProperty("continuationToken").GetProperty("type").GetString());
        Assert.Equal("firstsecond", projection.GetProperty("text").GetString());
        Assert.True(projection.GetProperty("additionalProperties").GetProperty("probe").GetBoolean());
        Assert.Equal(typeof(RawEnvelope).FullName, projection.GetProperty("rawRepresentationType").GetString());
        Assert.Equal(new[] { "first", "second" }, projection.GetProperty("contents").EnumerateArray().Select(content => content.GetProperty("text").GetString()));
    }

    [Fact]
    public void Project_FallbackSkipsCustomGettersWhileRetainingTheirNamesAndTypes()
    {
        JsonElement properties = JsonSerializer.SerializeToElement(ContentProjection.Project(new ThrowingContent()))
            .GetProperty("properties");

        Assert.Equal("visible", properties.GetProperty("safe").GetString());
        Assert.Equal(typeof(string).FullName, properties.GetProperty("throws").GetProperty("type").GetString());
    }

    [Fact]
    public void Project_FallbackLeavesUnknownEnumerablesOpaqueWithoutInvokingEnumerator()
    {
        ThrowingEnumerable values = new();
        JsonElement properties = JsonSerializer.SerializeToElement(
            ContentProjection.Project(new ThrowingEnumerableContent(values)))
            .GetProperty("properties");

        Assert.False(values.EnumeratorInvoked);
        Assert.Equal(["first", "second"], properties.GetProperty("safeValues").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(typeof(ThrowingEnumerable).FullName, properties.GetProperty("throwingValues").GetProperty("type").GetString());
    }

    [Fact]
    public void Project_CollectionBoundaryLeavesUnknownEnumerableOpaqueWithoutInvokingEnumerator()
    {
        ThrowingContentEnumerable contents = new();

        JsonElement projection = JsonSerializer.SerializeToElement(ContentProjection.Project(contents));

        Assert.False(contents.EnumeratorInvoked);
        Assert.Equal(
            typeof(ThrowingContentEnumerable).FullName,
            projection.GetProperty("contents").GetProperty("type").GetString());
    }

    [Fact]
    public void Project_AgentResponseUpdateLeavesCustomContentsListOpaqueWithoutInvokingEnumerator()
    {
        ThrowingList<AIContent> contents = new();
        AgentResponseUpdate update = new(ChatRole.Assistant, [new TextContent("initial")])
        {
            Contents = contents,
        };

        JsonElement projection = JsonSerializer.SerializeToElement(ContentProjection.Project(update));

        Assert.False(contents.EnumeratorInvoked);
        Assert.Equal(JsonValueKind.Null, projection.GetProperty("text").ValueKind);
        Assert.Equal(
            contents.GetType().FullName,
            projection.GetProperty("contents").GetProperty("type").GetString());
    }

    [Fact]
    public void Project_AgentResponseUpdateCapturesCompleteFrameworkOwnedContents()
    {
        const int contentCount = 101;
        AgentResponseUpdate update = new(
            ChatRole.Assistant,
            Enumerable.Range(0, contentCount).Select(index => new TextContent($"content-{index}")).ToArray());

        JsonElement projection = JsonSerializer.SerializeToElement(ContentProjection.Project(update));

        Assert.Equal(contentCount, projection.GetProperty("contents").GetArrayLength());
    }

    [Fact]
    public void Project_ToolContentLeavesCustomInputAndOutputListsOpaqueWithoutInvokingEnumerators()
    {
        ThrowingList<AIContent> mcpOutputs = new();
        ThrowingList<AIContent> codeInputs = new();
        ThrowingList<AIContent> codeOutputs = new();
        ThrowingList<AIContent> webOutputs = new();
        ThrowingList<AIContent> imageOutputs = new();
        (AIContent Content, string PropertyName, ThrowingList<AIContent> Values)[] cases =
        [
            (new McpServerToolResultContent("mcp-call") { Outputs = mcpOutputs }, "outputs", mcpOutputs),
            (new CodeInterpreterToolCallContent("code-call") { Inputs = codeInputs }, "inputs", codeInputs),
            (new CodeInterpreterToolResultContent("code-call") { Outputs = codeOutputs }, "outputs", codeOutputs),
            (new WebSearchToolResultContent("web-call") { Outputs = webOutputs }, "outputs", webOutputs),
            (new ImageGenerationToolResultContent("image-call") { Outputs = imageOutputs }, "outputs", imageOutputs),
        ];

        foreach ((AIContent content, string propertyName, ThrowingList<AIContent> values) in cases)
        {
            JsonElement projection = JsonSerializer.SerializeToElement(ContentProjection.Project(content));

            Assert.False(values.EnumeratorInvoked);
            Assert.Equal(
                values.GetType().FullName,
                projection.GetProperty(propertyName).GetProperty("type").GetString());
        }
    }

    [Fact]
    public void Project_CollectionPolicyDoesNotTrustBclWrappersAroundCustomCollections()
    {
        ThrowingList<AIContent> contents = new();
        ThrowingList<string> values = new();
        ThrowingDictionary<string, object?> properties = new();
        ReadOnlyCollection<AIContent> wrappedContents = new(contents);
        WrappedCollectionContent content = new(
            new ReadOnlyCollection<string>(values),
            new ReadOnlyDictionary<string, object?>(properties));

        JsonElement collectionProjection = JsonSerializer.SerializeToElement(ContentProjection.Project(wrappedContents));
        JsonElement fallback = JsonSerializer.SerializeToElement(ContentProjection.Project(content))
            .GetProperty("properties");

        Assert.False(contents.EnumeratorInvoked);
        Assert.False(values.EnumeratorInvoked);
        Assert.False(properties.Accessed);
        Assert.Equal(
            wrappedContents.GetType().FullName,
            collectionProjection.GetProperty("contents").GetProperty("type").GetString());
        Assert.Equal(
            content.Values.GetType().FullName,
            fallback.GetProperty("values").GetProperty("type").GetString());
        Assert.Equal(
            content.Properties.GetType().FullName,
            fallback.GetProperty("properties").GetProperty("type").GetString());
    }

    [Fact]
    public void Project_CollectionPolicyDoesNotTrustCollectionAroundCustomList()
    {
        ThrowingList<string> values = new();
        Collection<string> wrappedValues = new(values);
        CollectionContent content = new(wrappedValues);

        JsonElement properties = JsonSerializer.SerializeToElement(ContentProjection.Project(content))
            .GetProperty("properties");

        Assert.False(values.Accessed);
        Assert.False(values.EnumeratorInvoked);
        Assert.Equal(
            wrappedValues.GetType().FullName,
            properties.GetProperty("values").GetProperty("type").GetString());
    }

    [Fact]
    public void Project_CollectionPolicyRetainsCollectionBackedByList()
    {
        Collection<string> values = new(new List<string> { "first", "second" });
        CollectionContent content = new(values);

        JsonElement properties = JsonSerializer.SerializeToElement(ContentProjection.Project(content))
            .GetProperty("properties");

        Assert.Equal(
            new[] { "first", "second" },
            properties.GetProperty("values").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void Project_UsageDetailsProjectsEveryAdditionalCountInOrder()
    {
        AdditionalPropertiesDictionary<long> counts = new()
        {
            ["ordinary-count"] = 11,
            ["accessToken"] = 12,
            ["resp_0123456789abcdefghijk"] = 13,
        };
        UsageContent usage = new(new UsageDetails
        {
            InputTokenCount = 3,
            AdditionalCounts = counts,
        });

        JsonElement details = JsonSerializer.SerializeToElement(ContentProjection.Project(usage))
            .GetProperty("details");
        JsonElement additionalCounts = details.GetProperty("additionalCounts");

        Assert.Equal(
            counts.Keys,
            additionalCounts.EnumerateArray().Select(entry => entry.GetProperty("key").GetString()));
        Assert.Equal(
            counts.Values,
            additionalCounts.EnumerateArray().Select(entry => entry.GetProperty("value").GetInt64()));
        Assert.Equal(3, details.GetProperty("inputTokenCount").GetInt64());
    }

    private sealed class UnknownContent : AIContent
    {
        public object Opaque { get; } = new { value = "raw-secret" };

        public IReadOnlyList<int> Numbers { get; } = Enumerable.Range(1, 101).ToArray();
    }

    private sealed class ThrowingContent : AIContent
    {
        public string Safe { get; } = "visible";

        public string Throws => throw new InvalidOperationException("must not be invoked");
    }

    private sealed class ThrowingEnumerableContent : AIContent
    {
        public ThrowingEnumerableContent(ThrowingEnumerable throwingValues)
        {
            ThrowingValues = throwingValues;
        }

        public string[] SafeValues { get; } = ["first", "second"];

        public ThrowingEnumerable ThrowingValues { get; }
    }

    private sealed class WrappedCollectionContent(
        ReadOnlyCollection<string> values,
        ReadOnlyDictionary<string, object?> properties) : AIContent
    {
        public ReadOnlyCollection<string> Values { get; } = values;

        public ReadOnlyDictionary<string, object?> Properties { get; } = properties;
    }

    private sealed class CollectionContent(Collection<string> values) : AIContent
    {
        public Collection<string> Values { get; } = values;
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

    private sealed class ThrowingContentEnumerable : IEnumerable<AIContent>
    {
        public bool EnumeratorInvoked { get; private set; }

        public IEnumerator<AIContent> GetEnumerator()
        {
            EnumeratorInvoked = true;
            throw new InvalidOperationException("must not enumerate custom content iterables");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RawEnvelope;

    private static IEnumerable<string?> ProjectedTexts(JsonElement content, string propertyName)
        => content.GetProperty(propertyName).EnumerateArray().Select(item => item.GetProperty("text").GetString());

    private static IEnumerable<string?> StringValues(JsonElement content, string propertyName)
        => content.GetProperty(propertyName).EnumerateArray().Select(item => item.GetString());
}
