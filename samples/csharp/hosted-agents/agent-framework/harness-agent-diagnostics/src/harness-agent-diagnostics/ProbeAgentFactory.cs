extern alias azureidentity;

#pragma warning disable OPENAI001
#pragma warning disable MAAI001

using System.ClientModel.Primitives;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace HarnessAgentDiagnostics;

public static class ProbeAgentFactory
{
    private const string AgentName = "harness-agent-diagnostics";
    private const string WorkingFolder = "probe";
    private const string Description = "Diagnostic harness agent with one deterministic local compute probe and in-memory state only.";
    private const string Instructions = "Use compute_probe for deterministic numeric diagnostics, keep notes in the in-memory probe folder, and avoid any external access.";

    public static IChatClient CreateFoundryChatClient(ProbeConfiguration configuration)
        => CreateFoundryChatClient(configuration, FoundryResponsesClientBuilder.Instance);

    internal static IChatClient CreateFoundryChatClient(
        ProbeConfiguration configuration,
        IFoundryResponsesClientBuilder foundryResponsesClientBuilder)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(foundryResponsesClientBuilder);

        TokenCredential credential = new azureidentity::Azure.Identity.DefaultAzureCredential();

        return foundryResponsesClientBuilder
            .CreateProjectOpenAIClient(configuration.ProjectEndpoint, credential, retryCount: 3)
            .GetResponsesClient()
            .AsIChatClient(configuration.ModelDeployment);
    }

    public static ProbeAgentContext Create(IChatClient chatClient, string openTelemetrySourceName)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        if (string.IsNullOrWhiteSpace(openTelemetrySourceName))
        {
            throw new ArgumentException("OpenTelemetry source name must not be blank.", nameof(openTelemetrySourceName));
        }

        InMemoryAgentFileStore fileStore = new();
        FileMemoryProvider fileMemoryProvider = new(
            fileStore,
            _ => new FileMemoryState { WorkingFolder = WorkingFolder });

        HarnessAgent agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Id = AgentName,
            Name = AgentName,
            Description = Description,
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                Tools =
                [
                    AIFunctionFactory.Create(
                        (Func<string, int[], ProbeResult>)ProbeTool.ComputeProbe,
                        new AIFunctionFactoryOptions
                        {
                            Name = "compute_probe",
                            Description = "Sort a set of integers and return their sum for deterministic probe diagnostics.",
                        }),
                ],
            },
            DisableWebSearch = true,
            DisableFileAccess = true,
            DisableAgentSkillsProvider = true,
            DisableFileMemory = true,
            DisableTodoProvider = false,
            DisableAgentModeProvider = false,
            DisableOpenTelemetry = false,
            OpenTelemetrySourceName = openTelemetrySourceName,
            AIContextProviders =
            [
                fileMemoryProvider,
            ],
        });

        ChatClientAgent innerAgent = agent.GetService(typeof(ChatClientAgent)) as ChatClientAgent
            ?? throw new InvalidOperationException("Harness agent did not expose its inner chat client agent.");

        TodoProvider todoProvider = innerAgent.AIContextProviders?.OfType<TodoProvider>().SingleOrDefault()
            ?? throw new InvalidOperationException("Harness agent did not include the TodoProvider.");

        AgentModeProvider agentModeProvider = innerAgent.AIContextProviders?.OfType<AgentModeProvider>().SingleOrDefault()
            ?? throw new InvalidOperationException("Harness agent did not include the AgentModeProvider.");

        FileMemoryProvider resolvedFileMemoryProvider = innerAgent.AIContextProviders?.OfType<FileMemoryProvider>().SingleOrDefault()
            ?? throw new InvalidOperationException("Harness agent did not include the FileMemoryProvider.");

        if (!ReferenceEquals(fileMemoryProvider, resolvedFileMemoryProvider))
        {
            throw new InvalidOperationException("Harness agent did not use the expected FileMemoryProvider instance.");
        }

        return new ProbeAgentContext(agent, fileStore, todoProvider, agentModeProvider);
    }

    internal interface IFoundryResponsesClientBuilder
    {
        IProjectOpenAIClientAdapter CreateProjectOpenAIClient(Uri endpoint, TokenCredential credential, int retryCount);
    }

    internal interface IProjectOpenAIClientAdapter
    {
        IResponsesClientAdapter GetResponsesClient();
    }

    internal interface IResponsesClientAdapter
    {
        IChatClient AsIChatClient(string modelDeployment);
    }

    private sealed class FoundryResponsesClientBuilder : IFoundryResponsesClientBuilder
    {
        public static IFoundryResponsesClientBuilder Instance { get; } = new FoundryResponsesClientBuilder();

        public IProjectOpenAIClientAdapter CreateProjectOpenAIClient(Uri endpoint, TokenCredential credential, int retryCount)
        {
            AIProjectClientOptions options = new()
            {
                RetryPolicy = new ClientRetryPolicy(retryCount),
            };

            ProjectOpenAIClient projectOpenAIClient = new AIProjectClient(endpoint, credential, options).GetProjectOpenAIClient()
                ?? throw new InvalidOperationException("Foundry project client did not expose a ProjectOpenAIClient.");

            return new ProjectOpenAIClientAdapter(projectOpenAIClient);
        }

        private sealed class ProjectOpenAIClientAdapter(ProjectOpenAIClient projectOpenAIClient) : IProjectOpenAIClientAdapter
        {
            public IResponsesClientAdapter GetResponsesClient()
                => new ResponsesClientAdapter(projectOpenAIClient.GetResponsesClient());
        }

        private sealed class ResponsesClientAdapter(ResponsesClient responsesClient) : IResponsesClientAdapter
        {
            public IChatClient AsIChatClient(string modelDeployment)
                => responsesClient.AsIChatClient(modelDeployment);
        }
    }
}

public sealed class ProbeAgentContext
{
    public ProbeAgentContext(
        HarnessAgent agent,
        InMemoryAgentFileStore fileStore,
        TodoProvider todoProvider,
        AgentModeProvider agentModeProvider)
    {
        Agent = agent;
        FileStore = fileStore;
        TodoProvider = todoProvider;
        AgentModeProvider = agentModeProvider;
    }

    public HarnessAgent Agent { get; }

    public InMemoryAgentFileStore FileStore { get; }

    public TodoProvider TodoProvider { get; }

    public AgentModeProvider AgentModeProvider { get; }
}

#pragma warning restore MAAI001
#pragma warning restore OPENAI001
