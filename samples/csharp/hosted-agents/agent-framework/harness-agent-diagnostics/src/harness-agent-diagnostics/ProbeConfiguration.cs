namespace HarnessAgentDiagnostics;

public sealed class ProbeConfiguration
{
    public ProbeConfiguration(Uri projectEndpoint, string modelDeployment)
    {
        if (projectEndpoint is null)
        {
            throw new ArgumentNullException(nameof(projectEndpoint));
        }

        if (!projectEndpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("Project endpoint must be an absolute URI.", nameof(projectEndpoint));
        }

        if (string.IsNullOrWhiteSpace(modelDeployment))
        {
            throw new ArgumentException("Model deployment must not be blank.", nameof(modelDeployment));
        }

        ProjectEndpoint = projectEndpoint;
        ModelDeployment = modelDeployment.Trim();
    }

    public Uri ProjectEndpoint { get; }

    public string ModelDeployment { get; }

    public static ProbeConfiguration FromEnvironment(Func<string, string?>? environmentReader = null)
    {
        environmentReader ??= Environment.GetEnvironmentVariable;

        const string endpointVariable = "FOUNDRY_PROJECT_ENDPOINT";
        const string deploymentVariable = "AZURE_AI_MODEL_DEPLOYMENT_NAME";

        string? endpointValue = environmentReader(endpointVariable);
        if (string.IsNullOrWhiteSpace(endpointValue) || !Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException($"Missing or invalid required environment variable: {endpointVariable}.");
        }

        string? deploymentValue = environmentReader(deploymentVariable);
        if (string.IsNullOrWhiteSpace(deploymentValue))
        {
            throw new InvalidOperationException($"Missing or invalid required environment variable: {deploymentVariable}.");
        }

        return new ProbeConfiguration(endpoint, deploymentValue);
    }
}
