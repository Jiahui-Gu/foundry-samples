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
            ActivityTraceFlags.Recorded,
            traceState: "link-vendor=value");
        List<string> mutableTag = ["captured"];
        DateTimeOffset eventTimestamp = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        using (Activity? parent = source.StartActivity("parent", ActivityKind.Client))
        {
            Assert.NotNull(parent);
            parent.DisplayName = "parent display";
            parent.TraceStateString = "parent-vendor=value";
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
                child.DisplayName = "child display";
                child.SetTag("gen_ai.request.model", "gpt-4.1-mini");
                child.AddEvent(new ActivityEvent("response.output_text.delta", eventTimestamp, new ActivityTagsCollection
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
        Assert.Equal("harness-tests", snapshots[0].Source);
        Assert.Equal("child", snapshots[0].OperationName);
        Assert.Equal("child display", snapshots[0].DisplayName);
        Assert.Equal(ActivityKind.Internal, snapshots[0].Kind);
        Assert.Equal(ActivityTraceFlags.Recorded, snapshots[0].TraceFlags);
        Assert.Equal("parent-vendor=value", snapshots[0].TraceState);
        Assert.Equal("parent", snapshots[1].OperationName);
        Assert.Equal("parent display", snapshots[1].DisplayName);
        Assert.Equal(ActivityKind.Client, snapshots[1].Kind);
        Assert.Equal(ActivityTraceFlags.Recorded, snapshots[1].TraceFlags);
        Assert.Equal("parent-vendor=value", snapshots[1].TraceState);
        Assert.Equal("gpt-4.1-mini", snapshots[0].Tags["gen_ai.request.model"]);
        Assert.Equal(snapshots[1].TraceId, snapshots[0].TraceId);
        Assert.Equal(snapshots[1].SpanId, snapshots[0].ParentSpanId);
        Assert.NotNull(snapshots[0].ParentId);
        Assert.Contains(snapshots[1].SpanId.ToHexString(), snapshots[0].ParentId, StringComparison.Ordinal);
        Assert.NotEqual(default, snapshots[0].StartTimeUtc);
        Assert.True(snapshots[0].Duration >= TimeSpan.Zero);
        ActivityEventSnapshot activityEvent = Assert.Single(snapshots[0].Events);
        Assert.Equal("response.output_text.delta", activityEvent.Name);
        Assert.Equal(eventTimestamp, activityEvent.Timestamp);
        Assert.Equal(1, activityEvent.Tags["sequence"]);
        ActivityLinkSnapshot link = Assert.Single(snapshots[0].Links);
        Assert.Equal(linkedContext.TraceId, link.TraceId);
        Assert.Equal(linkedContext.SpanId, link.SpanId);
        Assert.Equal(ActivityTraceFlags.Recorded, link.TraceFlags);
        Assert.Equal("link-vendor=value", link.TraceState);
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
