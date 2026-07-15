using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarnessAgentDiagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace HarnessAgentDiagnostics.Tests;

public sealed class DirectProbeTests
{
    [Fact]
    public async Task HarnessRuntime_UsesPinnedSessionProviderAndFileStoreApisWithoutModelAccess()
    {
        ProbeAgentContext context = ProbeAgentFactory.Create(
            new ThrowingChatClient(),
            "tests.direct-probe.runtime");
        HarnessDirectProbeRuntime runtime = new(context);

        AgentSession session = await runtime.CreateSessionAsync(CancellationToken.None);
        Assert.Equal("plan", await runtime.GetModeAsync(session, CancellationToken.None));
        Assert.Empty(await runtime.GetTodosAsync(session, CancellationToken.None));
        Assert.Empty(await runtime.GetMemoryFilesAsync(CancellationToken.None));

        await runtime.SetModeAsync(session, "execute", CancellationToken.None);
        await context.FileStore.WriteAsync(
            "probe/experiment.md",
            "label=maf-probe; values=3,1,4",
            CancellationToken.None);

        Assert.Equal("execute", await runtime.GetModeAsync(session, CancellationToken.None));
        Assert.Equal(
            new ProbeMemoryFileSnapshot("experiment.md", "label=maf-probe; values=3,1,4"),
            Assert.Single(await runtime.GetMemoryFilesAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task RunAsync_HappyPathRecordsOrderedUpdatesSnapshotsStateActivitiesAndSummary()
    {
        string outputDirectory = CreateOutputDirectory();
        using ScriptedProbeRuntime runtime = ScriptedProbeRuntime.HappyPath("tests.direct-probe.happy");

        try
        {
            ProbeRunSummary summary;
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                DirectProbe probe = new(runtime, recorder, runtime.ActivitySourceName);
                summary = await probe.RunAsync();
            }

            Assert.Equal(1, runtime.CreateSessionCount);
            Assert.Equal([DirectProbePrompts.Plan, DirectProbePrompts.Execute], runtime.Prompts);
            Assert.Equal(2, summary.UpdateCount);
            Assert.Equal(3, summary.ContentCount);
            Assert.Equal(["FunctionResultContent", "TextContent"], summary.ObservedContentTypes);
            Assert.Equal("execute", summary.Mode);
            Assert.All(summary.Todos, todo => Assert.True(todo.IsComplete));
            Assert.Equal(
                new ProbeMemoryFileSnapshot("experiment.md", "label=maf-probe; values=3,1,4"),
                Assert.Single(summary.MemoryFiles));
            Assert.Equal(["plan.activity", "execute.activity"], summary.ActivityNames);
            Assert.False(summary.DriverFallbackUsed);
            Assert.False(summary.RecoveryTurnUsed);
            Assert.Empty(summary.Failures);
            Assert.Empty(summary.MissingSignals);

            JsonElement[] updates = await ReadRecordsAsync(
                Path.Combine(outputDirectory, "agent-response-updates.jsonl"));
            Assert.Collection(
                updates,
                update =>
                {
                    Assert.Equal("plan", update.GetProperty("phase").GetString());
                    Assert.Equal(1, update.GetProperty("turn").GetInt32());
                    Assert.Equal(0, update.GetProperty("updateIndex").GetInt32());
                    Assert.Equal("PLAN_READY", update.GetProperty("update").GetProperty("text").GetString());
                },
                update =>
                {
                    Assert.Equal("execute", update.GetProperty("phase").GetString());
                    Assert.Equal(2, update.GetProperty("turn").GetInt32());
                    Assert.Equal(0, update.GetProperty("updateIndex").GetInt32());
                    Assert.Equal(2, update.GetProperty("update").GetProperty("contents").GetArrayLength());
                });

            JsonElement[] snapshots = (await ReadRecordsAsync(
                    Path.Combine(outputDirectory, "provider-state.jsonl")))
                .Select(record => record.GetProperty("providerState"))
                .ToArray();
            Assert.Equal(
                ["initial", "after-plan", "after-execute", "final"],
                snapshots.Select(snapshot => snapshot.GetProperty("phase").GetString()));
            Assert.All(snapshots, snapshot => Assert.Equal(JsonValueKind.Array, snapshot.GetProperty("gaps").ValueKind));
            Assert.Equal("execute", snapshots[^1].GetProperty("mode").GetString());
            Assert.All(
                snapshots[^1].GetProperty("todos").EnumerateArray(),
                todo => Assert.True(todo.GetProperty("isComplete").GetBoolean()));
            Assert.Equal(
                "label=maf-probe; values=3,1,4",
                snapshots[^1].GetProperty("memoryFiles")[0].GetProperty("content").GetString());
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_ModeFailureUsesExactlyOneDriverFallbackAndOneRecovery()
    {
        string outputDirectory = CreateOutputDirectory();
        using ScriptedProbeRuntime runtime = ScriptedProbeRuntime.ModeFailure("tests.direct-probe.mode");

        try
        {
            ProbeRunSummary summary;
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                summary = await new DirectProbe(runtime, recorder, runtime.ActivitySourceName).RunAsync();
            }

            Assert.Equal(1, runtime.SetModeCount);
            Assert.Equal(3, runtime.Prompts.Count);
            Assert.Equal(DirectProbePrompts.Recovery, runtime.Prompts[^1]);
            Assert.True(summary.DriverFallbackUsed);
            Assert.True(summary.RecoveryTurnUsed);
            Assert.Equal("execute", summary.Mode);
            Assert.All(summary.Todos, todo => Assert.True(todo.IsComplete));

            JsonElement[] snapshots = (await ReadRecordsAsync(
                    Path.Combine(outputDirectory, "provider-state.jsonl")))
                .Select(record => record.GetProperty("providerState"))
                .ToArray();
            JsonElement fallback = Assert.Single(
                snapshots,
                snapshot => snapshot.GetProperty("phase").GetString() == "after-driver-fallback");
            Assert.Equal("driver-fallback", fallback.GetProperty("transitionSource").GetString());
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_RemainingTodosUsesAtMostOneRecoveryWithoutDriverFallback()
    {
        string outputDirectory = CreateOutputDirectory();
        using ScriptedProbeRuntime runtime = ScriptedProbeRuntime.RemainingTodos("tests.direct-probe.todos");

        try
        {
            ProbeRunSummary summary;
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                summary = await new DirectProbe(runtime, recorder, runtime.ActivitySourceName).RunAsync();
            }

            Assert.Equal(0, runtime.SetModeCount);
            Assert.Equal(3, runtime.Prompts.Count);
            Assert.Equal(1, runtime.Prompts.Count(prompt => prompt == DirectProbePrompts.Recovery));
            Assert.False(summary.DriverFallbackUsed);
            Assert.True(summary.RecoveryTurnUsed);
            Assert.All(summary.Todos, todo => Assert.True(todo.IsComplete));
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_RuntimeErrorPropagatesAndRecordsSafeFailureMetadata()
    {
        const string secret = "TOP-SECRET-RUNTIME-VALUE";
        string outputDirectory = CreateOutputDirectory();
        using ScriptedProbeRuntime runtime = ScriptedProbeRuntime.ExecuteFailure(
            "tests.direct-probe.failure",
            secret);

        try
        {
            await using (var recorder = new DiagnosticRecorder(outputDirectory))
            {
                DirectProbe probe = new(runtime, recorder, runtime.ActivitySourceName);

                InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => probe.RunAsync());

                Assert.Contains(secret, exception.Message, StringComparison.Ordinal);
            }

            string providerJsonl = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "provider-state.jsonl"));
            Assert.Contains("InvalidOperationException", providerJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, providerJsonl, StringComparison.Ordinal);
            Assert.DoesNotContain("StackTrace", providerJsonl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" at ", providerJsonl, StringComparison.Ordinal);
            Assert.Contains("probe:failed", providerJsonl, StringComparison.Ordinal);
        }
        finally
        {
            DeleteOutputDirectory(outputDirectory);
        }
    }

    private static async Task<JsonElement[]> ReadRecordsAsync(string path)
    {
        string[] lines = await File.ReadAllLinesAsync(path);
        return lines.Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    }

    private static string CreateOutputDirectory()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test-output", Guid.NewGuid().ToString("N"));
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

    private sealed class ScriptedProbeRuntime : IDirectProbeRuntime, IDisposable
    {
        private readonly ActivitySource _activitySource;
        private readonly Scenario _scenario;
        private readonly string? _failureSecret;
        private readonly List<TodoItem> _todos = [];
        private readonly List<ProbeMemoryFileSnapshot> _memoryFiles = [];
        private string _mode = "plan";

        private ScriptedProbeRuntime(string activitySourceName, Scenario scenario, string? failureSecret = null)
        {
            ActivitySourceName = activitySourceName;
            _activitySource = new ActivitySource(activitySourceName);
            _scenario = scenario;
            _failureSecret = failureSecret;
        }

        internal string ActivitySourceName { get; }

        internal int CreateSessionCount { get; private set; }

        internal int SetModeCount { get; private set; }

        internal List<string> Prompts { get; } = [];

        internal static ScriptedProbeRuntime HappyPath(string sourceName)
            => new(sourceName, Scenario.Happy);

        internal static ScriptedProbeRuntime ModeFailure(string sourceName)
            => new(sourceName, Scenario.ModeFailure);

        internal static ScriptedProbeRuntime RemainingTodos(string sourceName)
            => new(sourceName, Scenario.RemainingTodos);

        internal static ScriptedProbeRuntime ExecuteFailure(string sourceName, string secret)
            => new(sourceName, Scenario.ExecuteFailure, secret);

        public ValueTask<AgentSession> CreateSessionAsync(CancellationToken cancellationToken)
        {
            CreateSessionCount++;
            return ValueTask.FromResult<AgentSession>(new ScriptedSession());
        }

        public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
            string prompt,
            AgentSession session,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            bool isPlan = prompt == DirectProbePrompts.Plan;
            using Activity? activity = _activitySource.StartActivity(isPlan ? "plan.activity" : prompt == DirectProbePrompts.Execute ? "execute.activity" : "recovery.activity");

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (isPlan)
            {
                AddTodos();
                yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("PLAN_READY")]);
                yield break;
            }

            if (prompt == DirectProbePrompts.Execute)
            {
                if (_scenario == Scenario.ExecuteFailure)
                {
                    throw new InvalidOperationException($"{_failureSecret}\n at scripted-stack");
                }

                if (_scenario != Scenario.ModeFailure)
                {
                    _mode = "execute";
                }

                if (_scenario == Scenario.Happy)
                {
                    CompleteProbe();
                }
                else if (_scenario == Scenario.RemainingTodos)
                {
                    _todos[0].IsComplete = true;
                    _todos[1].IsComplete = true;
                }

                yield return new AgentResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new TextContent("""{"mode":"execute","sum":8}"""),
                        new FunctionResultContent("compute-1", new { sum = 8 }),
                    ]);
                yield break;
            }

            CompleteProbe();
            yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("""{"recovered":true}""")]);
        }

        public Task<string> GetModeAsync(AgentSession session, CancellationToken cancellationToken)
            => Task.FromResult(_mode);

        public Task SetModeAsync(
            AgentSession session,
            string mode,
            CancellationToken cancellationToken)
        {
            SetModeCount++;
            _mode = mode;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TodoItem>> GetTodosAsync(
            AgentSession session,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TodoItem>>(_todos);

        public Task<IReadOnlyList<ProbeMemoryFileSnapshot>> GetMemoryFilesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProbeMemoryFileSnapshot>>(_memoryFiles);

        public void Dispose() => _activitySource.Dispose();

        private void AddTodos()
        {
            _todos.Add(new TodoItem { Id = 1, Title = "Write probe memory", Description = "write" });
            _todos.Add(new TodoItem { Id = 2, Title = "Compute probe", Description = "compute" });
            _todos.Add(new TodoItem { Id = 3, Title = "Verify probe", Description = "verify" });
        }

        private void CompleteProbe()
        {
            _mode = "execute";
            foreach (TodoItem todo in _todos)
            {
                todo.IsComplete = true;
            }

            _memoryFiles.Clear();
            _memoryFiles.Add(new ProbeMemoryFileSnapshot("experiment.md", "label=maf-probe; values=3,1,4"));
        }

        private enum Scenario
        {
            Happy,
            ModeFailure,
            RemainingTodos,
            ExecuteFailure,
        }

        private sealed class ScriptedSession : AgentSession;
    }
}

#pragma warning restore MAAI001
