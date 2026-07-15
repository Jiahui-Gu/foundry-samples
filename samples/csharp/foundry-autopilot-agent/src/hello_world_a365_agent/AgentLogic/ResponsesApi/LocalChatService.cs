namespace HelloWorldA365.AgentLogic.ResponsesApi;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using HelloWorldA365.Models;

public sealed class LocalChatService(
    HttpClient httpClient,
    IConfiguration configuration,
    DefaultAzureCredential credential)
{
    public async Task<string> RespondAsync(string message, CancellationToken cancellationToken)
    {
        var endpoint = configuration["AzureOpenAIEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("AzureOpenAIEndpoint is not configured.");
        }

        var deployment = configuration["ModelDeployment"];
        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new InvalidOperationException("ModelDeployment is not configured.");
        }

        var requestBody = new
        {
            model = deployment,
            instructions = AgentInstructions.GetInstructions(new AgentMetadata()),
            input = message
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/openai/responses?api-version=2025-03-01-preview");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Responses API call failed with status {(int)response.StatusCode}: {responseContent}",
                inner: null,
                response.StatusCode);
        }

        if (!ResponsesApiResponseParser.TryExtractOutputText(responseContent, out var outputText) ||
            string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("The Responses API returned no assistant text.");
        }

        return outputText;
    }
}
