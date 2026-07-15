using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace HarnessAgentDiagnostics;

public sealed class ActivityCapture : IDisposable
{
    private readonly ConcurrentQueue<ActivitySnapshot> _snapshots = new();
    private readonly string _harnessSourceName;
    private readonly ActivityListener _listener;
    private bool _disposed;

    public ActivityCapture(string harnessSourceName)
    {
        if (string.IsNullOrWhiteSpace(harnessSourceName))
        {
            throw new ArgumentException("Harness source name must not be blank.", nameof(harnessSourceName));
        }

        _harnessSourceName = harnessSourceName;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => IsCapturedSource(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _snapshots.Enqueue(ActivitySnapshot.Create(activity)),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<ActivitySnapshot> Drain()
    {
        List<ActivitySnapshot> drained = [];
        while (_snapshots.TryDequeue(out ActivitySnapshot? snapshot))
        {
            drained.Add(snapshot);
        }

        return drained;
    }

    public async Task DrainToAsync(DiagnosticRecorder recorder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        foreach (ActivitySnapshot snapshot in Drain())
        {
            await recorder.RecordActivityAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener.Dispose();
    }

    private bool IsCapturedSource(string sourceName)
        => sourceName.Equals(_harnessSourceName, StringComparison.Ordinal)
            || sourceName.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal)
            || sourceName.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal);
}

public sealed record ActivitySnapshot(
    string Source,
    string OperationName,
    string DisplayName,
    ActivityKind Kind,
    ActivityStatusCode Status,
    string? StatusDescription,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivitySpanId ParentSpanId,
    string? ParentId,
    ActivityTraceFlags TraceFlags,
    string? TraceState,
    DateTime StartTimeUtc,
    TimeSpan Duration,
    IReadOnlyDictionary<string, object?> Tags,
    IReadOnlyList<ActivityBaggageEntrySnapshot> Baggage,
    IReadOnlyList<ActivityEventSnapshot> Events,
    IReadOnlyList<ActivityLinkSnapshot> Links)
{
    internal static ActivitySnapshot Create(Activity activity)
        => new(
            activity.Source.Name,
            activity.OperationName,
            activity.DisplayName,
            activity.Kind,
            activity.Status,
            activity.StatusDescription,
            activity.TraceId,
            activity.SpanId,
            activity.ParentSpanId,
            activity.ParentId,
            activity.ActivityTraceFlags,
            activity.TraceStateString,
            activity.StartTimeUtc,
            activity.Duration,
            AsReadOnlyDictionary(activity.TagObjects),
            activity.Baggage.Select(ActivityBaggageEntrySnapshot.Create).ToArray(),
            activity.Events.Select(ActivityEventSnapshot.Create).ToArray(),
            activity.Links.Select(ActivityLinkSnapshot.Create).ToArray());

    internal static IReadOnlyDictionary<string, object?> AsReadOnlyDictionary(IEnumerable<KeyValuePair<string, object?>>? values)
    {
        Dictionary<string, object?> copy = new(StringComparer.Ordinal);
        int count = 0;
        foreach (KeyValuePair<string, object?> pair in values ?? [])
        {
            if (count++ == SafeCollectionPolicy.MaximumElements)
            {
                break;
            }

            copy[pair.Key] = SnapshotValue(pair.Value);
        }

        return new ReadOnlyDictionary<string, object?>(copy);
    }

    private static object? SnapshotValue(object? value, int depth = 0)
    {
        if (value is null
            || value is string
            || value is bool
            || value.GetType().IsPrimitive
            || value is decimal
            || value is DateTime
            || value is DateTimeOffset
            || value is TimeSpan
            || value is Guid
            || value is Uri
            || value.GetType().IsEnum)
        {
            return value;
        }

        if (depth == 8)
        {
            return new ActivityOpaqueValue(value.GetType().FullName);
        }

        if (value is IDictionary dictionary && SafeCollectionPolicy.IsSafeDictionary(value))
        {
            Dictionary<string, object?> copy = new(StringComparer.Ordinal);
            int count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is string key)
                {
                    if (count++ == SafeCollectionPolicy.MaximumElements)
                    {
                        break;
                    }

                    copy[key] = SnapshotValue(entry.Value, depth + 1);
                }
            }

            return new ReadOnlyDictionary<string, object?>(copy);
        }

        if (value is IEnumerable sequence && SafeCollectionPolicy.IsSafeSequence(value))
        {
            List<object?> copy = [];
            int count = 0;
            foreach (object? item in sequence)
            {
                if (count++ == SafeCollectionPolicy.MaximumElements)
                {
                    break;
                }

                copy.Add(SnapshotValue(item, depth + 1));
            }

            return new ReadOnlyCollection<object?>(copy);
        }

        return new ActivityOpaqueValue(value.GetType().FullName);
    }
}

public sealed record ActivityOpaqueValue(string? Type);

public sealed record ActivityBaggageEntrySnapshot(string Key, string? Value)
{
    internal static ActivityBaggageEntrySnapshot Create(KeyValuePair<string, string?> baggage)
        => new(baggage.Key, baggage.Value);
}

public sealed record ActivityEventSnapshot(
    string Name,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Tags)
{
    internal static ActivityEventSnapshot Create(ActivityEvent activityEvent)
        => new(activityEvent.Name, activityEvent.Timestamp, ActivitySnapshot.AsReadOnlyDictionary(activityEvent.Tags));
}

public sealed record ActivityLinkSnapshot(
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivityTraceFlags TraceFlags,
    string? TraceState,
    IReadOnlyDictionary<string, object?> Tags)
{
    internal static ActivityLinkSnapshot Create(ActivityLink link)
        => new(
            link.Context.TraceId,
            link.Context.SpanId,
            link.Context.TraceFlags,
            link.Context.TraceState,
            ActivitySnapshot.AsReadOnlyDictionary(link.Tags));
}
