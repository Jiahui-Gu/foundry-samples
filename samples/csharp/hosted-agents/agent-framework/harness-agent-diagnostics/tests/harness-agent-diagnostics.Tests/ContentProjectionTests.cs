using System.Text.Json;
using HarnessAgentDiagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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

    private sealed class RawEnvelope;
}
