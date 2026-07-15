using HarnessAgentDiagnostics;
using System.Reflection;
using Azure.Core;
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

    [Fact]
    public void CreateFoundryChatClient_ComposesResponsesClientPathWithPinnedCredentialAndRetryCount()
    {
        ProbeConfiguration configuration = new(new Uri("https://example.test"), "gpt-4.1-mini");
        RecordingFoundryResponsesClientBuilder builder = new();

        IChatClient chatClient = ProbeAgentFactory.CreateFoundryChatClient(configuration, builder);

        Assert.Same(builder.ChatClient, chatClient);
        Assert.Equal(configuration.ProjectEndpoint, builder.Endpoint);
        Assert.NotNull(builder.Credential);
        Assert.Equal("Azure.Identity.DefaultAzureCredential", builder.Credential!.GetType().FullName);
        Assert.Equal("Azure.Identity", builder.Credential.GetType().Assembly.GetName().Name);
        Assert.Equal(new Version(1, 20, 0, 0), builder.Credential.GetType().Assembly.GetName().Version);
        Assert.Equal(3, builder.RetryCount);
        Assert.Equal(configuration.ModelDeployment, builder.ModelDeployment);
        Assert.Equal(
            ["CreateProjectOpenAIClient", "GetResponsesClient", "AsIChatClient"],
            builder.Calls);
    }

    [Fact]
    public void CreateFoundryChatClient_ConstructsPublicClientWithoutNetwork()
    {
        ProbeConfiguration configuration = new(new Uri("https://example.test"), "gpt-4.1-mini");

        using IChatClient chatClient = ProbeAgentFactory.CreateFoundryChatClient(configuration);

        Assert.NotNull(chatClient);
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

    private sealed class RecordingFoundryResponsesClientBuilder : ProbeAgentFactory.IFoundryResponsesClientBuilder
    {
        public List<string> Calls { get; } = [];

        public TokenCredential? Credential { get; private set; }

        public Uri? Endpoint { get; private set; }

        public IChatClient ChatClient { get; } = new ThrowingChatClient();

        public string? ModelDeployment { get; private set; }

        public int RetryCount { get; private set; }

        public ProbeAgentFactory.IProjectOpenAIClientAdapter CreateProjectOpenAIClient(Uri endpoint, TokenCredential credential, int retryCount)
        {
            Endpoint = endpoint;
            Credential = credential;
            RetryCount = retryCount;
            Calls.Add("CreateProjectOpenAIClient");
            return new RecordingProjectOpenAIClient(this);
        }

        private sealed class RecordingProjectOpenAIClient(RecordingFoundryResponsesClientBuilder owner) : ProbeAgentFactory.IProjectOpenAIClientAdapter
        {
            public ProbeAgentFactory.IResponsesClientAdapter GetResponsesClient()
            {
                owner.Calls.Add("GetResponsesClient");
                return new RecordingResponsesClient(owner);
            }
        }

        private sealed class RecordingResponsesClient(RecordingFoundryResponsesClientBuilder owner) : ProbeAgentFactory.IResponsesClientAdapter
        {
            public IChatClient AsIChatClient(string modelDeployment)
            {
                owner.ModelDeployment = modelDeployment;
                owner.Calls.Add("AsIChatClient");
                return owner.ChatClient;
            }
        }
    }
}

#pragma warning restore MAAI001
