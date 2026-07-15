using System.Collections;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MEAI001

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
            ["additionalProperties"] = ProjectAdditionalProperties(content.AdditionalProperties),
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
                result["uri"] = data.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : ProjectSafeValue(data.Uri);
                result["mediaType"] = data.MediaType;
                result["name"] = data.Name;
                result["dataLength"] = data.Data.Length;
                break;
            case UriContent uri:
                result["uri"] = ProjectSafeValue(uri.Uri);
                result["mediaType"] = uri.MediaType;
                break;
            case HostedFileContent hostedFile:
                result["fileId"] = hostedFile.FileId;
                result["mediaType"] = hostedFile.MediaType;
                result["name"] = hostedFile.Name;
                result["sizeInBytes"] = hostedFile.SizeInBytes;
                result["createdAt"] = ProjectSafeValue(hostedFile.CreatedAt);
                result["purpose"] = hostedFile.Purpose;
                result["scope"] = hostedFile.Scope;
                break;
            case HostedVectorStoreContent hostedVectorStore:
                result["vectorStoreId"] = hostedVectorStore.VectorStoreId;
                break;
            case McpServerToolCallContent mcpCall:
                result["callId"] = mcpCall.CallId;
                result["name"] = mcpCall.Name;
                result["serverName"] = mcpCall.ServerName;
                result["arguments"] = ProjectSafeValue(mcpCall.Arguments);
                break;
            case McpServerToolResultContent mcpResult:
                result["callId"] = mcpResult.CallId;
                result["outputs"] = ProjectContents(mcpResult.Outputs);
                break;
            case CodeInterpreterToolCallContent codeInterpreterCall:
                result["callId"] = codeInterpreterCall.CallId;
                result["inputs"] = ProjectContents(codeInterpreterCall.Inputs);
                break;
            case CodeInterpreterToolResultContent codeInterpreterResult:
                result["callId"] = codeInterpreterResult.CallId;
                result["outputs"] = ProjectContents(codeInterpreterResult.Outputs);
                break;
            case WebSearchToolCallContent webSearchCall:
                result["callId"] = webSearchCall.CallId;
                result["queries"] = ProjectSafeValue(webSearchCall.Queries);
                break;
            case WebSearchToolResultContent webSearchResult:
                result["callId"] = webSearchResult.CallId;
                result["outputs"] = ProjectContents(webSearchResult.Outputs);
                break;
            case ImageGenerationToolCallContent imageGenerationCall:
                result["callId"] = imageGenerationCall.CallId;
                break;
            case ImageGenerationToolResultContent imageGenerationResult:
                result["callId"] = imageGenerationResult.CallId;
                result["outputs"] = ProjectContents(imageGenerationResult.Outputs);
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

    private static JsonArray ProjectContents(IEnumerable<AIContent>? contents)
        => contents is null ? [] : new JsonArray(contents.Select(Project).ToArray());

    public static JsonObject Project(AgentResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        return new JsonObject
        {
            ["type"] = update.GetType().Name,
            ["role"] = update.Role?.ToString(),
            ["authorName"] = update.AuthorName,
            ["agentId"] = update.AgentId,
            ["responseId"] = update.ResponseId,
            ["messageId"] = update.MessageId,
            ["createdAt"] = ProjectSafeValue(update.CreatedAt),
            ["finishReason"] = update.FinishReason?.ToString(),
            ["continuationToken"] = ProjectContinuationToken(update.ContinuationToken),
            ["text"] = update.Text,
            ["additionalProperties"] = ProjectAdditionalProperties(update.AdditionalProperties),
            ["rawRepresentationType"] = update.RawRepresentation?.GetType().FullName,
            ["contents"] = new JsonArray(update.Contents.Select(Project).ToArray()),
        };
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
    {
        JsonObject result = new();
        foreach (PropertyInfo property in content.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0
                || property.Name is nameof(AIContent.RawRepresentation)
                    or "Exception"
                    or "StackTrace"
                    or "DebuggerDisplay")
            {
                continue;
            }

            string name = ToCamelCase(property.Name);
            if (!HasCompilerGeneratedAutoPropertyGetter(property))
            {
                result[name] = OpaqueType(property.PropertyType);
                continue;
            }

            object? value = property.GetValue(content);
            result[name] = ProjectSafeValue(value);
        }

        return result;
    }

    private static JsonObject ProjectContinuationToken(ResponseContinuationToken? token)
        => token is null
            ? new JsonObject()
            : new JsonObject
            {
                ["type"] = token.GetType().FullName,
                ["byteLength"] = token.ToBytes().Length,
                ["value"] = Convert.ToBase64String(token.ToBytes().ToArray()),
            };

    private static JsonObject ProjectAdditionalProperties(IEnumerable<KeyValuePair<string, object?>>? additionalProperties)
    {
        JsonObject result = new();
        if (additionalProperties is null)
        {
            return result;
        }

        foreach ((string key, object? value) in additionalProperties)
        {
            result[key] = ProjectSafeValue(value);
        }

        return result;
    }

    private static bool HasCompilerGeneratedAutoPropertyGetter(PropertyInfo property)
    {
        Type? declaringType = property.DeclaringType;
        bool compilerGenerated = property.GetMethod?.IsDefined(typeof(CompilerGeneratedAttribute), inherit: true) == true
            || declaringType?.IsDefined(typeof(CompilerGeneratedAttribute), inherit: true) == true;
        return compilerGenerated
            && (declaringType?.GetField(
                    $"<{property.Name}>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic) is not null
                || declaringType?.GetField(
                    $"<{property.Name}>i__Field",
                    BindingFlags.Instance | BindingFlags.NonPublic) is not null);
    }

    private static JsonObject OpaqueType(Type type)
        => new() { ["type"] = type.FullName };

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

        if (TryProjectSafeDictionary(value, depth, out JsonObject? dictionaryNode))
        {
            return dictionaryNode;
        }

        if (TryProjectSafeSequence(value, depth, out JsonArray? sequenceNode))
        {
            return sequenceNode;
        }

        return new JsonObject { ["type"] = value.GetType().FullName };
    }

    private static bool TryProjectSafeDictionary(object value, int depth, out JsonObject? node)
    {
        node = null;
        if (value is not IDictionary dictionary || !IsSafeDictionaryType(value.GetType()))
        {
            return false;
        }

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

        node = result;
        return true;
    }

    private static bool TryProjectSafeSequence(object value, int depth, out JsonArray? node)
    {
        node = null;
        if (value is byte[] || value is not IEnumerable sequence || !IsSafeSequenceType(value.GetType()))
        {
            return false;
        }

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

        node = result;
        return true;
    }

    private static bool IsSafeSequenceType(Type type)
        => type.IsArray
            || IsExactGenericDefinition(type, typeof(List<>))
            || IsExactGenericDefinition(type, typeof(Collection<>))
            || IsExactGenericDefinition(type, typeof(ReadOnlyCollection<>));

    private static bool IsSafeDictionaryType(Type type)
        => IsExactGenericDefinition(type, typeof(Dictionary<,>))
            || IsExactGenericDefinition(type, typeof(SortedDictionary<,>))
            || IsExactGenericDefinition(type, typeof(SortedList<,>))
            || IsExactGenericDefinition(type, typeof(ReadOnlyDictionary<,>));

    private static bool IsExactGenericDefinition(Type type, Type definition)
        => type.IsGenericType && type.GetGenericTypeDefinition() == definition;

    private static string ToCamelCase(string value)
        => string.Concat(char.ToLowerInvariant(value[0]), value[1..]);
}
