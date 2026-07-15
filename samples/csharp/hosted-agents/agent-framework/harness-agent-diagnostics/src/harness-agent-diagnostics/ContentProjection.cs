using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace HarnessAgentDiagnostics;

public static class ContentProjection
{
    public static JsonObject Project(IEnumerable<AIContent> contents)
        => new()
        {
            ["contents"] = new JsonArray(contents.Select(Project).ToArray()),
        };

    public static JsonObject Project(AIContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        JsonObject result = new()
        {
            ["type"] = content.GetType().Name,
            ["annotations"] = ProjectSafeValue(content.Annotations),
            ["additionalProperties"] = ProjectSafeValue(content.AdditionalProperties),
            ["rawRepresentationType"] = content.RawRepresentation?.GetType().FullName,
        };

        switch (content)
        {
            case TextContent text:
                result["text"] = text.Text;
                break;
            case TextReasoningContent reasoning:
                result["text"] = reasoning.Text;
                result["hasProtectedData"] = !string.IsNullOrEmpty(reasoning.ProtectedData);
                result["protectedDataLength"] = reasoning.ProtectedData?.Length ?? 0;
                break;
            case FunctionCallContent functionCall:
                result["callId"] = functionCall.CallId;
                result["name"] = functionCall.Name;
                result["arguments"] = ProjectSafeValue(functionCall.Arguments);
                result["informationalOnly"] = functionCall.InformationalOnly;
                break;
            case FunctionResultContent functionResult:
                result["callId"] = functionResult.CallId;
                result["result"] = ProjectSafeValue(functionResult.Result);
                break;
            case UsageContent usage:
                result["details"] = ProjectUsageDetails(usage.Details);
                break;
            case ErrorContent error:
                result["message"] = error.Message;
                result["errorCode"] = error.ErrorCode;
                result["details"] = ProjectSafeValue(error.Details);
                break;
            case DataContent data:
                result["uri"] = ProjectSafeValue(data.Uri);
                result["mediaType"] = data.MediaType;
                result["name"] = data.Name;
                result["dataLength"] = data.Data.Length;
                break;
            case UriContent uri:
                result["uri"] = ProjectSafeValue(uri.Uri);
                result["mediaType"] = uri.MediaType;
                break;
            case ToolApprovalRequestContent approvalRequest:
                result["requestId"] = approvalRequest.RequestId;
                result["toolCall"] = Project(approvalRequest.ToolCall);
                break;
            case ToolApprovalResponseContent approvalResponse:
                result["requestId"] = approvalResponse.RequestId;
                result["approved"] = approvalResponse.Approved;
                result["reason"] = approvalResponse.Reason;
                result["toolCall"] = Project(approvalResponse.ToolCall);
                break;
            default:
                result["properties"] = ProjectFallbackProperties(content);
                break;
        }

        return result;
    }

    public static JsonObject Project(AgentResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        JsonObject result = ProjectSafePublicProperties(update);
        result["type"] = update.GetType().Name;
        result["contents"] = new JsonArray(GetContents(update).Select(Project).ToArray());
        return result;
    }

    private static JsonObject ProjectUsageDetails(UsageDetails? details)
    {
        JsonObject result = new();
        if (details is null)
        {
            return result;
        }

        foreach (PropertyInfo property in typeof(UsageDetails).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            result[ToCamelCase(property.Name)] = ProjectSafeValue(property.GetValue(details));
        }

        return result;
    }

    private static JsonObject ProjectFallbackProperties(AIContent content)
        => ProjectSafePublicProperties(content);

    private static JsonObject ProjectSafePublicProperties(object source)
    {
        JsonObject result = new();
        foreach (PropertyInfo property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0
                || property.Name is nameof(AIContent.RawRepresentation)
                    or "Exception"
                    or "StackTrace"
                    or "DebuggerDisplay")
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(source);
            }
            catch (Exception)
            {
                continue;
            }

            result[ToCamelCase(property.Name)] = ProjectSafeValue(value);
        }

        return result;
    }

    private static IEnumerable<AIContent> GetContents(AgentResponseUpdate update)
    {
        PropertyInfo? property = update.GetType().GetProperty("Contents", BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(update) as IEnumerable<AIContent> ?? [];
    }

    private static JsonNode? ProjectSafeValue(object? value, int depth = 0)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string or bool or char
            || value.GetType().IsPrimitive
            || value is decimal
            || value is DateTime
            || value is DateTimeOffset
            || value is TimeSpan
            || value is Guid
            || value is Uri
            || value.GetType().IsEnum)
        {
            return value is Uri uri
                ? JsonValue.Create(uri.OriginalString)
                : JsonSerializer.SerializeToNode(value, value.GetType());
        }

        if (depth == 8)
        {
            return new JsonObject { ["type"] = value.GetType().FullName };
        }

        if (value is IDictionary dictionary)
        {
            JsonObject result = new();
            int count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is string key)
                {
                    if (count++ == 100)
                    {
                        result["truncated"] = true;
                        break;
                    }

                    result[key] = ProjectSafeValue(entry.Value, depth + 1);
                }
            }

            return result;
        }

        if (value is IEnumerable sequence && value is not byte[])
        {
            JsonArray result = [];
            int count = 0;
            foreach (object? item in sequence)
            {
                if (count++ == 100)
                {
                    result.Add(new JsonObject { ["truncated"] = true });
                    break;
                }

                result.Add(ProjectSafeValue(item, depth + 1));
            }

            return result;
        }

        return new JsonObject { ["type"] = value.GetType().FullName };
    }

    private static string ToCamelCase(string value)
        => string.Concat(char.ToLowerInvariant(value[0]), value[1..]);
}
