using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public class ProbeConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void FromEnvironment_ThrowsWhenProjectEndpointMissingOrBlank(string? endpoint)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ProbeConfiguration.FromEnvironment(name => name switch
        {
            "FOUNDRY_PROJECT_ENDPOINT" => endpoint,
            "AZURE_AI_MODEL_DEPLOYMENT_NAME" => "gpt-4.1-mini",
            _ => null,
        }));

        Assert.Contains("FOUNDRY_PROJECT_ENDPOINT", exception.Message);
    }

    [Fact]
    public void FromEnvironment_ThrowsWhenProjectEndpointIsNotAbsoluteUri()
    {
        const string invalidEndpoint = "/relative-endpoint";

        var exception = Assert.Throws<InvalidOperationException>(() => ProbeConfiguration.FromEnvironment(name => name switch
        {
            "FOUNDRY_PROJECT_ENDPOINT" => invalidEndpoint,
            "AZURE_AI_MODEL_DEPLOYMENT_NAME" => "gpt-4.1-mini",
            _ => null,
        }));

        Assert.Contains("FOUNDRY_PROJECT_ENDPOINT", exception.Message);
        Assert.DoesNotContain(invalidEndpoint, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void FromEnvironment_ThrowsWhenModelDeploymentMissingOrBlank(string? deployment)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ProbeConfiguration.FromEnvironment(name => name switch
        {
            "FOUNDRY_PROJECT_ENDPOINT" => "https://example.services.ai.azure.com/api/projects/demo",
            "AZURE_AI_MODEL_DEPLOYMENT_NAME" => deployment,
            _ => null,
        }));

        Assert.Contains("AZURE_AI_MODEL_DEPLOYMENT_NAME", exception.Message);
    }

    [Fact]
    public void FromEnvironment_ReturnsConfigurationFromInjectedValues()
    {
        List<string> requestedVariables = [];

        var configuration = ProbeConfiguration.FromEnvironment(name =>
        {
            requestedVariables.Add(name);

            return name switch
            {
                "FOUNDRY_PROJECT_ENDPOINT" => "https://example.services.ai.azure.com/api/projects/demo",
                "AZURE_AI_MODEL_DEPLOYMENT_NAME" => "gpt-4.1-mini",
                _ => null,
            };
        });

        Assert.Equal(
            ["FOUNDRY_PROJECT_ENDPOINT", "AZURE_AI_MODEL_DEPLOYMENT_NAME"],
            requestedVariables);
        Assert.Equal(new Uri("https://example.services.ai.azure.com/api/projects/demo"), configuration.ProjectEndpoint);
        Assert.Equal("gpt-4.1-mini", configuration.ModelDeployment);
    }
}
