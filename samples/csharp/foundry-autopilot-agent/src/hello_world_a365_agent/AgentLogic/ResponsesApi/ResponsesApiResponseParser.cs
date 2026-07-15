namespace HelloWorldA365.AgentLogic.ResponsesApi;

using System.Text;
using System.Text.Json;

internal static class ResponsesApiResponseParser
{
    public static bool TryExtractOutputText(string responseJson, out string outputText)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var textParts = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                    !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("type", out var contentType) &&
                        contentType.GetString() == "output_text" &&
                        contentItem.TryGetProperty("text", out var text))
                    {
                        textParts.Append(text.GetString());
                    }
                }
            }

            outputText = textParts.ToString();
            return outputText.Length > 0;
        }

        if (root.TryGetProperty("output_text", out var simpleText))
        {
            outputText = simpleText.GetString() ?? string.Empty;
            return outputText.Length > 0;
        }

        outputText = string.Empty;
        return false;
    }
}
