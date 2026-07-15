using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace HarnessAgentDiagnostics;

public sealed class DirectProbe
{
    private readonly IDirectProbeRuntime _runtime;
    private readonly DiagnosticRecorder _recorder;
    private readonly string _activitySourceName;

    public DirectProbe(ProbeAgentContext context, DiagnosticRecorder recorder)
        : this(
            new HarnessDirectProbeRuntime(context ?? throw new ArgumentNullException(nameof(context))),
            recorder,
            context.OpenTelemetrySourceName)
    {
    }

    internal DirectProbe(
        IDirectProbeRuntime runtime,
        DiagnosticRecorder recorder,
        string activitySourceName)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        if (string.IsNullOrWhiteSpace(activitySourceName))
        {
            throw new ArgumentException("Activity source name must not be blank.", nameof(activitySourceName));
        }

        _activitySourceName = activitySourceName;
    }

    public async Task<ProbeRunSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        List<string> contentTypes = [];
        List<ProbeFailure> failures = [];
        List<ActivitySnapshot> activities = [];
        List<ExceptionDispatchInfo> secondaryErrors = [];
        ProbeProviderSnapshot? lastSnapshot = null;
        ExceptionDispatchInfo? primaryError = null;
        int updateCount = 0;
        int contentCount = 0;
        int turn = 0;
        bool driverFallbackUsed = false;
        bool recoveryTurnUsed = false;

        using ActivityCapture activityCapture = new(_activitySourceName);
        AgentSession? session = null;

        try
        {
            session = await _runtime.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            lastSnapshot = await RecordSnapshotAsync(
                "initial",
                turn,
                "none",
                session,
                failures,
                cancellationToken).ConfigureAwait(false);

            turn++;
            (int updates, int contents) = await RunTurnAsync(
                "plan",
                turn,
                DirectProbePrompts.Plan,
                session,
                contentTypes,
                cancellationToken).ConfigureAwait(false);
            updateCount += updates;
            contentCount += contents;
            lastSnapshot = await RecordSnapshotAsync(
                "after-plan",
                turn,
                "model",
                session,
                failures,
                cancellationToken).ConfigureAwait(false);

            turn++;
            (updates, contents) = await RunTurnAsync(
                "execute",
                turn,
                DirectProbePrompts.Execute,
                session,
                contentTypes,
                cancellationToken).ConfigureAwait(false);
            updateCount += updates;
            contentCount += contents;
            lastSnapshot = await RecordSnapshotAsync(
                "after-execute",
                turn,
                "model",
                session,
                failures,
                cancellationToken).ConfigureAwait(false);

            if (!string.Equals(lastSnapshot.Mode, "execute", StringComparison.Ordinal))
            {
                await _runtime.SetModeAsync(session, "execute", cancellationToken).ConfigureAwait(false);
                driverFallbackUsed = true;
                lastSnapshot = await RecordSnapshotAsync(
                    "after-driver-fallback",
                    turn,
                    "driver-fallback",
                    session,
                    failures,
                    cancellationToken).ConfigureAwait(false);
            }

            if (driverFallbackUsed || lastSnapshot.Todos.Any(todo => !todo.IsComplete))
            {
                recoveryTurnUsed = true;
                turn++;
                (updates, contents) = await RunTurnAsync(
                    "recovery",
                    turn,
                    DirectProbePrompts.Recovery,
                    session,
                    contentTypes,
                    cancellationToken).ConfigureAwait(false);
                updateCount += updates;
                contentCount += contents;
            }

            lastSnapshot = await RecordSnapshotAsync(
                "final",
                turn,
                "none",
                session,
                failures,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(new ProbeFailure(PhaseForTurn(turn), exception.GetType().Name));
            primaryError = ExceptionDispatchInfo.Capture(exception);

            ProbeProviderSnapshot? failureSnapshot = null;
            string snapshotGap;
            if (session is null)
            {
                snapshotGap = "failure-snapshot:unavailable";
            }
            else
            {
                try
                {
                    failureSnapshot = await CaptureSnapshotAsync(
                        "final",
                        turn,
                        "none",
                        session,
                        failures,
                        ["probe:failed"],
                        CancellationToken.None).ConfigureAwait(false);
                    lastSnapshot = failureSnapshot;
                    snapshotGap = string.Empty;
                }
                catch (Exception snapshotException)
                {
                    secondaryErrors.Add(ExceptionDispatchInfo.Capture(snapshotException));
                    failures.Add(new ProbeFailure("failure-snapshot", snapshotException.GetType().Name));
                    snapshotGap = "failure-snapshot:failed";
                }
            }

            try
            {
                if (failureSnapshot is not null)
                {
                    await RecordProviderSnapshotAsync(failureSnapshot, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _recorder.RecordProviderStateAsync(
                        CreateFailureEvidence(turn, failures, snapshotGap),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception evidenceException)
            {
                secondaryErrors.Add(ExceptionDispatchInfo.Capture(evidenceException));
            }
        }
        finally
        {
            try
            {
                activities.AddRange(activityCapture.Drain());
            }
            catch (Exception drainException)
            {
                secondaryErrors.Add(ExceptionDispatchInfo.Capture(drainException));
            }

            foreach (ActivitySnapshot activity in activities)
            {
                try
                {
                    await _recorder.RecordActivityAsync(activity, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception activityException)
                {
                    secondaryErrors.Add(ExceptionDispatchInfo.Capture(activityException));
                }
            }
        }

        ThrowIfFailed(primaryError, secondaryErrors);
        if (lastSnapshot is null)
        {
            throw new InvalidOperationException("The probe did not capture provider state.");
        }

        IReadOnlyList<string> missingSignals = FindMissingSignals(
            lastSnapshot,
            updateCount,
            activities.Count);

        return new ProbeRunSummary(
            updateCount,
            contentCount,
            contentTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            lastSnapshot.Mode,
            lastSnapshot.Todos,
            lastSnapshot.MemoryFiles,
            activities.Select(activity => activity.OperationName).ToArray(),
            driverFallbackUsed,
            recoveryTurnUsed,
            failures,
            missingSignals);
    }

    private async Task<(int Updates, int Contents)> RunTurnAsync(
        string phase,
        int turn,
        string prompt,
        AgentSession session,
        List<string> contentTypes,
        CancellationToken cancellationToken)
    {
        int updateIndex = 0;
        int contentCount = 0;
        await foreach (AgentResponseUpdate update in _runtime
            .RunStreamingAsync(prompt, session, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            await _recorder.RecordAgentResponseUpdateAsync(
                phase,
                turn,
                updateIndex,
                update,
                cancellationToken).ConfigureAwait(false);

            foreach (AIContent content in update.Contents)
            {
                contentTypes.Add(content.GetType().Name);
                contentCount++;
            }

            updateIndex++;
        }

        return (updateIndex, contentCount);
    }

    private async Task<ProbeProviderSnapshot> RecordSnapshotAsync(
        string phase,
        int turn,
        string transitionSource,
        AgentSession session,
        IReadOnlyList<ProbeFailure> failures,
        CancellationToken cancellationToken)
    {
        ProbeProviderSnapshot snapshot = await CaptureSnapshotAsync(
            phase,
            turn,
            transitionSource,
            session,
            failures,
            gaps: null,
            cancellationToken).ConfigureAwait(false);
        await RecordProviderSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async Task<ProbeProviderSnapshot> CaptureSnapshotAsync(
        string phase,
        int turn,
        string transitionSource,
        AgentSession session,
        IReadOnlyList<ProbeFailure> failures,
        IEnumerable<string>? gaps,
        CancellationToken cancellationToken)
    {
        string mode = await _runtime.GetModeAsync(session, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TodoItem> liveTodos = await _runtime.GetTodosAsync(session, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProbeMemoryFileSnapshot> liveMemory =
            await _runtime.GetMemoryFilesAsync(cancellationToken).ConfigureAwait(false);

        ProbeProviderSnapshot snapshot = new(
            phase,
            turn,
            mode,
            liveTodos.Select(todo => new ProbeTodoSnapshot(
                todo.Id,
                todo.Title,
                todo.Description,
                todo.IsComplete)),
            liveMemory.Select(file => new ProbeMemoryFileSnapshot(file.Path, file.Content)),
            transitionSource,
            failures,
            gaps);
        return snapshot;
    }

    private Task RecordProviderSnapshotAsync(
        ProbeProviderSnapshot snapshot,
        CancellationToken cancellationToken)
        => _recorder.RecordProviderStateAsync(CreateProviderState(snapshot), cancellationToken);

    private static JsonObject CreateProviderState(ProbeProviderSnapshot snapshot)
        => new()
        {
            ["phase"] = snapshot.Phase,
            ["turn"] = snapshot.Turn,
            ["mode"] = snapshot.Mode,
            ["todos"] = new JsonArray(snapshot.Todos.Select(todo => new JsonObject
            {
                ["id"] = todo.Id,
                ["title"] = todo.Title,
                ["description"] = todo.Description,
                ["isComplete"] = todo.IsComplete,
            }).ToArray()),
            ["memoryFiles"] = new JsonArray(snapshot.MemoryFiles.Select(file => new JsonObject
            {
                ["path"] = file.Path,
                ["content"] = file.Content,
            }).ToArray()),
            ["transitionSource"] = snapshot.TransitionSource,
            ["failures"] = CreateFailures(snapshot.Failures),
            ["gaps"] = new JsonArray(snapshot.Gaps.Select(gap => JsonValue.Create(gap)).ToArray()),
        };

    private static JsonObject CreateFailureEvidence(
        int turn,
        IReadOnlyList<ProbeFailure> failures,
        string snapshotGap)
        => new()
        {
            ["phase"] = "final",
            ["turn"] = turn,
            ["transitionSource"] = "none",
            ["failures"] = CreateFailures(failures),
            ["gaps"] = new JsonArray(
                new[] { "probe:failed", snapshotGap }
                   .Where(gap => !string.IsNullOrEmpty(gap))
                   .Select(gap => JsonValue.Create(gap))
                   .ToArray()),
        };

    private static JsonArray CreateFailures(IEnumerable<ProbeFailure> failures)
        => new(failures.Select(failure => new JsonObject
        {
            ["phase"] = failure.Phase,
            ["kind"] = failure.Kind,
        }).ToArray());

    private static void ThrowIfFailed(
        ExceptionDispatchInfo? primaryError,
        IReadOnlyList<ExceptionDispatchInfo> secondaryErrors)
    {
        if (primaryError is not null)
        {
            if (secondaryErrors.Count == 0)
            {
                primaryError.Throw();
            }

            throw new AggregateException(
                "The probe failed and secondary diagnostic failures occurred.",
                new[] { primaryError.SourceException }
                   .Concat(secondaryErrors.Select(error => error.SourceException)));
        }

        if (secondaryErrors.Count == 1)
        {
            secondaryErrors[0].Throw();
        }

        if (secondaryErrors.Count > 1)
        {
            throw new AggregateException(
                "Multiple diagnostic failures occurred.",
                secondaryErrors.Select(error => error.SourceException));
        }
    }

    private static IReadOnlyList<string> FindMissingSignals(
        ProbeProviderSnapshot snapshot,
        int updateCount,
        int activityCount)
    {
        List<string> missing = [];
        if (!string.Equals(snapshot.Mode, "execute", StringComparison.Ordinal))
        {
            missing.Add("mode:execute");
        }

        string[] expectedTitles = ["Write probe memory", "Compute probe", "Verify probe"];
        if (snapshot.Todos.Count != expectedTitles.Length
            || !snapshot.Todos.Select(todo => todo.Title).SequenceEqual(expectedTitles, StringComparer.Ordinal)
            || snapshot.Todos.Any(todo => !todo.IsComplete))
        {
            missing.Add("todos:expected-three-complete");
        }

        if (snapshot.MemoryFiles.SingleOrDefault(
                file => string.Equals(file.Path, "experiment.md", StringComparison.Ordinal))
            is not { Content: "label=maf-probe; values=3,1,4" })
        {
            missing.Add("memory:experiment.md");
        }

        if (updateCount == 0)
        {
            missing.Add("updates:none");
        }

        if (activityCount == 0)
        {
            missing.Add("activities:none");
        }

        return missing.AsReadOnly();
    }

    private static string PhaseForTurn(int turn)
        => turn switch
        {
            <= 0 => "initial",
            1 => "plan",
            2 => "execute",
            _ => "recovery",
        };
}

internal interface IDirectProbeRuntime
{
    ValueTask<AgentSession> CreateSessionAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        string prompt,
        AgentSession session,
        CancellationToken cancellationToken);

    Task<string> GetModeAsync(AgentSession session, CancellationToken cancellationToken);

    Task SetModeAsync(AgentSession session, string mode, CancellationToken cancellationToken);

    Task<IReadOnlyList<TodoItem>> GetTodosAsync(
        AgentSession session,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProbeMemoryFileSnapshot>> GetMemoryFilesAsync(
        CancellationToken cancellationToken);
}

internal sealed class HarnessDirectProbeRuntime : IDirectProbeRuntime
{
    private readonly ProbeAgentContext _context;

    internal HarnessDirectProbeRuntime(ProbeAgentContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ValueTask<AgentSession> CreateSessionAsync(CancellationToken cancellationToken)
        => _context.Agent.CreateSessionAsync(cancellationToken);

    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        string prompt,
        AgentSession session,
        CancellationToken cancellationToken)
        => _context.Agent.RunStreamingAsync(prompt, session, options: null, cancellationToken);

    public Task<string> GetModeAsync(AgentSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_context.AgentModeProvider.GetMode(session));
    }

    public Task SetModeAsync(
        AgentSession session,
        string mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context.AgentModeProvider.SetMode(session, mode);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TodoItem>> GetTodosAsync(
        AgentSession session,
        CancellationToken cancellationToken)
        => _context.TodoProvider.GetAllTodosAsync(session, cancellationToken);

    public async Task<IReadOnlyList<ProbeMemoryFileSnapshot>> GetMemoryFilesAsync(
        CancellationToken cancellationToken)
    {
        List<ProbeMemoryFileSnapshot> files = [];
        Stack<string> directories = new();
        directories.Push(string.Empty);

        while (directories.Count > 0)
        {
            string relativeDirectory = directories.Pop();
            string storeDirectory = CombineStorePath(ProbeAgentFactory.WorkingFolder, relativeDirectory);
            IReadOnlyList<FileStoreEntry> children =
                await _context.FileStore.ListChildrenAsync(storeDirectory, cancellationToken).ConfigureAwait(false);

            foreach (FileStoreEntry entry in children.OrderByDescending(entry => entry.Name, StringComparer.Ordinal))
            {
                ValidateEntry(entry);
                string relativePath = CombineStorePath(relativeDirectory, entry.Name);
                if (string.Equals(entry.Type, FileStoreEntry.Directory, StringComparison.Ordinal))
                {
                    directories.Push(relativePath);
                    continue;
                }

                if (!string.Equals(entry.Type, FileStoreEntry.File, StringComparison.Ordinal))
                {
                    continue;
                }

                if (files.Count == SafeCollectionPolicy.MaximumElements)
                {
                    throw new InvalidOperationException("Probe file memory exceeded the safe file limit.");
                }

                string storePath = CombineStorePath(ProbeAgentFactory.WorkingFolder, relativePath);
                string content = await _context.FileStore.ReadAsync(storePath, cancellationToken).ConfigureAwait(false)
                    ?? string.Empty;
                files.Add(new ProbeMemoryFileSnapshot(relativePath, content));
            }
        }

        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static string CombineStorePath(string left, string right)
        => string.IsNullOrEmpty(left)
            ? right
            : string.IsNullOrEmpty(right)
                ? left
                : $"{left}/{right}";

    private static void ValidateEntry(FileStoreEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)
            || entry.Name is "." or ".."
            || entry.Name.Contains('/', StringComparison.Ordinal)
            || entry.Name.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Probe file memory returned an unsafe entry name.");
        }
    }
}

internal sealed class ProbeProviderSnapshot
{
    internal ProbeProviderSnapshot(
        string phase,
        int turn,
        string mode,
        IEnumerable<ProbeTodoSnapshot> todos,
        IEnumerable<ProbeMemoryFileSnapshot> memoryFiles,
        string transitionSource,
        IEnumerable<ProbeFailure> failures,
        IEnumerable<string>? gaps = null)
    {
        Phase = phase;
        Turn = turn;
        Mode = mode;
        Todos = Array.AsReadOnly(todos.ToArray());
        MemoryFiles = Array.AsReadOnly(memoryFiles.ToArray());
        TransitionSource = transitionSource;
        Failures = Array.AsReadOnly(failures.ToArray());
        Gaps = Array.AsReadOnly((gaps ?? []).ToArray());
    }

    public string Phase { get; }

    public int Turn { get; }

    public string Mode { get; }

    public IReadOnlyList<ProbeTodoSnapshot> Todos { get; }

    public IReadOnlyList<ProbeMemoryFileSnapshot> MemoryFiles { get; }

    public string TransitionSource { get; }

    public IReadOnlyList<ProbeFailure> Failures { get; }

    public IReadOnlyList<string> Gaps { get; }
}

public sealed record ProbeTodoSnapshot(int Id, string Title, string? Description, bool IsComplete);

public sealed record ProbeMemoryFileSnapshot(string Path, string Content);

public sealed record ProbeFailure(string Phase, string Kind);

public sealed class ProbeRunSummary
{
    internal ProbeRunSummary(
        int updateCount,
        int contentCount,
        IEnumerable<string> observedContentTypes,
        string mode,
        IEnumerable<ProbeTodoSnapshot> todos,
        IEnumerable<ProbeMemoryFileSnapshot> memoryFiles,
        IEnumerable<string> activityNames,
        bool driverFallbackUsed,
        bool recoveryTurnUsed,
        IEnumerable<ProbeFailure> failures,
        IEnumerable<string> missingSignals)
    {
        UpdateCount = updateCount;
        ContentCount = contentCount;
        ObservedContentTypes = Array.AsReadOnly(observedContentTypes.ToArray());
        Mode = mode;
        Todos = Array.AsReadOnly(todos.ToArray());
        MemoryFiles = Array.AsReadOnly(memoryFiles.ToArray());
        ActivityNames = Array.AsReadOnly(activityNames.ToArray());
        DriverFallbackUsed = driverFallbackUsed;
        RecoveryTurnUsed = recoveryTurnUsed;
        Failures = Array.AsReadOnly(failures.ToArray());
        MissingSignals = Array.AsReadOnly(missingSignals.ToArray());
    }

    public int UpdateCount { get; }

    public int ContentCount { get; }

    public IReadOnlyList<string> ObservedContentTypes { get; }

    public string Mode { get; }

    public IReadOnlyList<ProbeTodoSnapshot> Todos { get; }

    public IReadOnlyList<ProbeMemoryFileSnapshot> MemoryFiles { get; }

    public IReadOnlyList<string> ActivityNames { get; }

    public bool DriverFallbackUsed { get; }

    public bool RecoveryTurnUsed { get; }

    public IReadOnlyList<ProbeFailure> Failures { get; }

    public IReadOnlyList<string> MissingSignals { get; }
}

#pragma warning restore MAAI001
