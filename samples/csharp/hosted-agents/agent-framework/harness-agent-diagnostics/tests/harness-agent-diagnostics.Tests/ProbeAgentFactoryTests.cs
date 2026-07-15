using HarnessAgentDiagnostics;
using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace HarnessAgentDiagnostics.Tests;

public class ProbeAgentFactoryTests
{
    [Fact]
    public async Task Create_ReturnsHarnessAgentContextWithExpectedServicesAndTooling()
    {
        var context = ProbeAgentFactory.Create(new ThrowingChatClient(), "tests.probe");
        ChatClientAgent innerAgent = Assert.IsType<ChatClientAgent>(context.Agent.GetService(typeof(ChatClientAgent))!);

        Assert.Equal("harness-agent-diagnostics", innerAgent.Name);
        Assert.Contains("diagnostic", innerAgent.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("diagnostic", innerAgent.Instructions, StringComparison.OrdinalIgnoreCase);

        ChatOptions chatOptions = Assert.IsType<ChatOptions>(context.Agent.GetService(typeof(ChatOptions))!);
        AITool tool = Assert.Single(chatOptions.Tools!);
        Assert.Equal("compute_probe", tool.Name);

        Assert.NotNull(innerAgent.AIContextProviders);
        Assert.Collection(
            innerAgent.AIContextProviders!,
            provider => Assert.Same(context.TodoProvider, Assert.IsType<TodoProvider>(provider)),
            provider => Assert.Same(context.AgentModeProvider, Assert.IsType<AgentModeProvider>(provider)),
            provider => Assert.IsType<FileMemoryProvider>(provider));

        FileMemoryProvider fileMemoryProvider = Assert.IsType<FileMemoryProvider>(innerAgent.AIContextProviders![2]);
        FieldInfo storeField = typeof(FileMemoryProvider).GetField("_fileStore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Same(context.FileStore, storeField.GetValue(fileMemoryProvider));

        AgentSession session = await context.Agent.CreateSessionAsync(CancellationToken.None);
        FieldInfo sessionStateField = typeof(FileMemoryProvider).GetField("_sessionState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? sessionState = sessionStateField.GetValue(fileMemoryProvider);
        Assert.NotNull(sessionState);
        object? fileMemoryState = sessionState.GetType().GetMethod("GetOrInitializeState")!.Invoke(sessionState, [session]);
        Assert.NotNull(fileMemoryState);
        string? workingFolder = fileMemoryState.GetType().GetProperty("WorkingFolder")!.GetValue(fileMemoryState) as string;

        Assert.Equal("probe", workingFolder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_ThrowsWhenOpenTelemetrySourceNameIsBlank(string? openTelemetrySourceName)
    {
        var exception = Assert.Throws<ArgumentException>(() => ProbeAgentFactory.Create(new ThrowingChatClient(), openTelemetrySourceName!));

        Assert.Contains("openTelemetrySourceName", exception.ParamName, StringComparison.OrdinalIgnoreCase);
    }
}

#pragma warning restore MAAI001
