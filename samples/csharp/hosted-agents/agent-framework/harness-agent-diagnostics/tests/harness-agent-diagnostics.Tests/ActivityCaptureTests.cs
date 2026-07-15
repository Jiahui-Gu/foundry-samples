using System.Diagnostics;
using System.Text.Json;
using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public sealed class ActivityCaptureTests
{
    [Fact]
    public void Drain_CapturesMatchingParentChildActivitiesInStopOrderAndIgnoresOtherSources()
    {
        using ActivityCapture capture = new("harness-tests");
        using ActivitySource source = new("harness-tests");
        using ActivitySource unrelatedSource = new("unrelated-tests");
        ActivityContext linkedContext = new(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded);
        List<string> mutableTag = ["captured"];

        using (Activity? parent = source.StartActivity("parent", ActivityKind.Client))
        {
            Assert.NotNull(parent);
            parent.SetTag("gen_ai.operation.name", "chat");
            parent.SetTag("mutable", mutableTag);
            parent.AddBaggage("request", "resp_0123456789abcdefghijk");
            parent.SetStatus(ActivityStatusCode.Error, "completed");

            using (Activity? child = source.StartActivity(
                "child",
                ActivityKind.Internal,
                parent.Context,
                links: [new ActivityLink(linkedContext, new ActivityTagsCollection
                {
                    ["link.tag"] = "linked",
                })]))
            {
                Assert.NotNull(child);
                child.SetTag("gen_ai.request.model", "gpt-4.1-mini");
                child.AddEvent(new ActivityEvent("response.output_text.delta", tags: new ActivityTagsCollection
                {
                    ["sequence"] = 1,
                }));
            }
        }

        using (Activity? ignored = unrelatedSource.StartActivity("ignored"))
        {
            Assert.Null(ignored);
        }

        mutableTag.Add("mutated-after-stop");
        IReadOnlyList<ActivitySnapshot> snapshots = capture.Drain();

        Assert.Equal(2, snapshots.Count);
        Assert.Equal("child", snapshots[0].OperationName);
        Assert.Equal("parent", snapshots[1].OperationName);
        Assert.Equal("gpt-4.1-mini", snapshots[0].Tags["gen_ai.request.model"]);
        Assert.Equal(snapshots[1].TraceId, snapshots[0].TraceId);
        Assert.Equal(snapshots[1].SpanId, snapshots[0].ParentSpanId);
        Assert.NotNull(snapshots[0].ParentId);
        Assert.Contains(snapshots[1].SpanId.ToHexString(), snapshots[0].ParentId, StringComparison.Ordinal);
        Assert.NotEqual(default, snapshots[0].StartTimeUtc);
        Assert.True(snapshots[0].Duration >= TimeSpan.Zero);
        ActivityEventSnapshot activityEvent = Assert.Single(snapshots[0].Events);
        Assert.Equal("response.output_text.delta", activityEvent.Name);
        Assert.Equal(1, activityEvent.Tags["sequence"]);
        ActivityLinkSnapshot link = Assert.Single(snapshots[0].Links);
        Assert.Equal(linkedContext.TraceId, link.TraceId);
        Assert.Equal(linkedContext.SpanId, link.SpanId);
        Assert.Equal("linked", link.Tags["link.tag"]);
        Assert.Equal(ActivityStatusCode.Error, snapshots[1].Status);
        Assert.Equal("completed", snapshots[1].StatusDescription);
        Assert.Equal("resp_0123456789abcdefghijk", snapshots[1].Baggage["request"]);
        Assert.DoesNotContain("mutated-after-stop", JsonSerializer.Serialize(snapshots[1].Tags["mutable"]), StringComparison.Ordinal);
        Assert.NotEqual(default, snapshots[0].TraceId);
        Assert.NotEqual(default, snapshots[0].SpanId);
        Assert.True(capture.Drain().Count == 0);
    }
}
