using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;

namespace HarnessAgentDiagnostics;

public sealed class DiagnosticRecorder : IAsyncDisposable
{
    private const string AgentResponseUpdatesFileName = "agent-response-updates.jsonl";
    private const string ProviderStateFileName = "provider-state.jsonl";
    private const string ActivitiesFileName = "activities.jsonl";

    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.Ordinal);
    private readonly SensitiveValueSanitizer _sanitizer;
    private TaskCompletionSource? _operationsDrained;
    private Task? _disposeTask;
    private bool _disposeStarted;
    private int _activeOperations;
    private long _nextRecordSequence;

    public DiagnosticRecorder(string outputDirectory, IEnumerable<string>? sensitiveValues = null)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory must not be blank.", nameof(outputDirectory));
        }

        OutputDirectory = outputDirectory;
        Directory.CreateDirectory(OutputDirectory);
        _sanitizer = new SensitiveValueSanitizer(sensitiveValues);
    }

    public string OutputDirectory { get; }

    public Task RecordAgentResponseUpdateAsync(AgentResponseUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return WriteAsync(AgentResponseUpdatesFileName, () => ContentProjection.Project(update), cancellationToken);
    }

    public Task RecordProviderStateAsync(object providerState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerState);
        if (providerState is AgentResponseUpdate)
        {
            throw new ArgumentException("Agent response updates must be recorded with the strongly typed update method.", nameof(providerState));
        }

        return WriteAsync(ProviderStateFileName, () => providerState, cancellationToken);
    }

    public Task RecordActivityAsync(ActivitySnapshot activity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return WriteAsync(ActivitiesFileName, () => ProjectActivity(activity), cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_lifecycleLock)
        {
            if (_disposeTask is null)
            {
                _disposeStarted = true;
                Task operationsDrained = Task.CompletedTask;
                if (_activeOperations != 0)
                {
                    _operationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    operationsDrained = _operationsDrained.Task;
                }

                _disposeTask = DisposeCoreAsync(operationsDrained);
            }

            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync(Task operationsDrained)
    {
        await operationsDrained.ConfigureAwait(false);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (StreamWriter writer in _writers.Values)
            {
                await writer.FlushAsync().ConfigureAwait(false);
                writer.Dispose();
            }

            _writers.Clear();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private Task WriteAsync(string fileName, Func<object> valueFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);

        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            _activeOperations++;
        }

        return WriteAcceptedAsync(fileName, valueFactory, cancellationToken);
    }

    private async Task WriteAcceptedAsync(
        string fileName,
        Func<object> valueFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                object value = valueFactory();
                JsonNode? node = CreateSafeNode(value);
                JsonNode sanitizedNode = _sanitizer.Sanitize(node) ?? JsonValue.Create((string?)null)!;
                JsonObject record = sanitizedNode as JsonObject ?? new JsonObject { ["value"] = sanitizedNode };
                record["recordSequence"] = ++_nextRecordSequence;
                string json = record.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                StreamWriter writer = GetWriter(fileName);
                await writer.WriteLineAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            CompleteAcceptedOperation();
        }
    }

    private void CompleteAcceptedOperation()
    {
        TaskCompletionSource? operationsDrained = null;
        lock (_lifecycleLock)
        {
            _activeOperations--;
            if (_disposeStarted && _activeOperations == 0)
            {
                operationsDrained = _operationsDrained;
            }
        }

        operationsDrained?.TrySetResult();
    }

    private StreamWriter GetWriter(string fileName)
    {
        if (_writers.TryGetValue(fileName, out StreamWriter? writer))
        {
            return writer;
        }

        FileStream stream = new(
            Path.Combine(OutputDirectory, fileName),
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        writer = new StreamWriter(stream);
        _writers.Add(fileName, writer);
        return writer;
    }

    private static JsonNode? CreateSafeNode(object? value, int depth = 0)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonNode node)
        {
            return node.DeepClone();
        }

        Type type = value.GetType();
        if (value is Exception || IsCredentialOrToken(type))
        {
            return new JsonObject { ["type"] = type.FullName };
        }

        if (value is string or bool or char
            || type.IsPrimitive
            || value is decimal
            || value is DateTime
            || value is DateTimeOffset
            || value is TimeSpan
            || value is Guid
            || value is Uri
            || type.IsEnum)
        {
            return value is Uri uri
                ? JsonValue.Create(uri.OriginalString)
                : JsonSerializer.SerializeToNode(value, type);
        }

        if (depth == 8)
        {
            return new JsonObject { ["type"] = type.FullName };
        }

        if (value is IDictionary dictionary)
        {
            JsonObject result = new();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is string key && !IsExcludedProperty(key))
                {
                    result[key] = CreateSafeNode(entry.Value, depth + 1);
                }
            }

            return result;
        }

        if (value is IEnumerable sequence)
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

                result.Add(CreateSafeNode(item, depth + 1));
            }

            return result;
        }

        JsonObject properties = new();
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || IsExcludedProperty(property.Name))
            {
                continue;
            }

            if (!HasCompilerGeneratedAutoPropertyGetter(property))
            {
                properties[property.Name] = new JsonObject { ["type"] = property.PropertyType.FullName };
                continue;
            }

            object? propertyValue = property.GetValue(value);
            properties[property.Name] = CreateSafeNode(propertyValue, depth + 1);
        }

        return properties;
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

    private static bool IsCredentialOrToken(Type type)
        => type.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Token", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedProperty(string name)
        => name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("authorizationHeader", StringComparison.OrdinalIgnoreCase)
            || name.Contains("header", StringComparison.OrdinalIgnoreCase)
            || name.Equals("stackTrace", StringComparison.OrdinalIgnoreCase)
            || name.Equals("exception", StringComparison.OrdinalIgnoreCase);

    private static object ProjectActivity(ActivitySnapshot activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return new
        {
            activity.Source,
            activity.OperationName,
            activity.DisplayName,
            activity.Kind,
            activity.Status,
            activity.StatusDescription,
            traceId = activity.TraceId.ToHexString(),
            spanId = activity.SpanId.ToHexString(),
            parentSpanId = activity.ParentSpanId.ToHexString(),
            activity.ParentId,
            activity.TraceFlags,
            activity.TraceState,
            activity.StartTimeUtc,
            activity.Duration,
            tags = activity.Tags.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            baggage = activity.Baggage.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            events = activity.Events.Select(activityEvent => new
            {
                activityEvent.Name,
                activityEvent.Timestamp,
                tags = activityEvent.Tags.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            }),
            links = activity.Links.Select(link => new
            {
                traceId = link.TraceId.ToHexString(),
                spanId = link.SpanId.ToHexString(),
                link.TraceFlags,
                link.TraceState,
                tags = link.Tags.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            }),
        };
    }
}

internal sealed partial class SensitiveValueSanitizer
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly List<string> _registeredValues;
    private int _nextAlias;

    public SensitiveValueSanitizer(IEnumerable<string>? registeredValues)
    {
        _registeredValues = registeredValues?
            .Where(value => !string.IsNullOrEmpty(value))
            .OrderByDescending(value => value.Length)
            .ToList() ?? [];
    }

    public JsonNode? Sanitize(JsonNode? node, string? propertyName = null)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            foreach ((string name, JsonNode? child) in obj.ToList())
            {
                if (IsContinuationTokenProperty(propertyName)
                    && name.Equals("value", StringComparison.Ordinal)
                    && child is JsonValue continuationValue
                    && continuationValue.TryGetValue<string>(out string? tokenValue))
                {
                    obj[name] = JsonValue.Create(GetAlias("continuation-token", tokenValue));
                    continue;
                }

                if (IsForbiddenProperty(name, child))
                {
                    obj.Remove(name);
                    continue;
                }

                if (IsSensitiveValueProperty(name))
                {
                    obj[name] = JsonValue.Create("[REDACTED]");
                    continue;
                }

                JsonNode? sanitizedChild = Sanitize(child, name);
                if (!ReferenceEquals(child, sanitizedChild))
                {
                    obj[name] = sanitizedChild;
                }
            }

            return obj;
        }

        if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                JsonNode? child = array[index];
                JsonNode? sanitizedChild = Sanitize(child, propertyName);
                if (!ReferenceEquals(child, sanitizedChild))
                {
                    array[index] = sanitizedChild;
                }
            }

            return array;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
        {
            return JsonValue.Create(SanitizeString(text, propertyName));
        }

        return node;
    }

    private string SanitizeString(string text, string? propertyName)
    {
        string sanitized = text;
        foreach (string registeredValue in _registeredValues)
        {
            sanitized = sanitized.Replace(registeredValue, GetAlias("sensitive", registeredValue), StringComparison.Ordinal);
        }

        if (IsContinuationTokenProperty(propertyName))
        {
            return GetAlias("continuation-token", sanitized);
        }

        sanitized = BearerTokenRegex().Replace(sanitized, "******");
        sanitized = JwtRegex().Replace(sanitized, "[REDACTED]");
        sanitized = ApiKeyRegex().Replace(sanitized, "[REDACTED]");
        sanitized = CredentialAssignmentRegex().Replace(sanitized, "${name}=[REDACTED]");
        sanitized = AzureResourceIdRegex().Replace(sanitized, match => GetAlias("azure-resource", match.Value));
        sanitized = OpenAiIdentifierRegex().Replace(sanitized, match => GetAlias(GetOpenAiIdentifierCategory(match.Value), match.Value));
        sanitized = ToolIdentifierRegex().Replace(sanitized, match => GetAlias("tool", match.Value));

        if (IsGuidIdentifierProperty(propertyName))
        {
            sanitized = GuidRegex().Replace(sanitized, match => GetAlias("guid", match.Value));
        }

        if (IsActivityIdentifierProperty(propertyName))
        {
            sanitized = HexIdentifierRegex().Replace(sanitized, match => GetAlias("otel-id", match.Value));
        }

        return sanitized;
    }

    private string GetAlias(string category, string value)
    {
        if (_aliases.TryGetValue(value, out string? alias))
        {
            return alias;
        }

        alias = $"{category}-{++_nextAlias}";
        _aliases.Add(value, alias);
        return alias;
    }

    private static string GetOpenAiIdentifierCategory(string value)
        => value[..value.IndexOf('_')] switch
        {
            "resp" => "response",
            "msg" => "message",
            "item" => "item",
            "call" or "fc" => "tool-call",
            _ => "openai-id",
        };

    private static bool IsForbiddenProperty(string name, JsonNode? value)
    {
        string normalized = NormalizePropertyName(name);
        if (normalized == "rawrepresentationtype")
        {
            return value is not null
                && (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out _));
        }

        if (normalized.Contains("rawrepresentation", StringComparison.Ordinal)
            || normalized is "authorization" or "authorizationheader"
            || normalized.Contains("header", StringComparison.Ordinal)
            || normalized.Contains("exception", StringComparison.Ordinal)
            || normalized.Contains("stack", StringComparison.Ordinal))
        {
            return true;
        }

        return value is JsonObject or JsonArray
            && (IsCredentialBearingProperty(normalized)
                || normalized.Contains("transport", StringComparison.Ordinal));
    }

    private static bool IsSensitiveValueProperty(string name)
    {
        string normalized = NormalizePropertyName(name);
        return IsCredentialBearingProperty(normalized);
    }

    private static string NormalizePropertyName(string name)
        => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool IsContinuationTokenProperty(string? propertyName)
        => propertyName is not null
            && NormalizePropertyName(propertyName) == "continuationtoken";

    private static bool IsCredentialBearingProperty(string normalized)
    {
        if (normalized == "continuationtoken")
        {
            return false;
        }

        return normalized is "authorization"
            or "authorizationheader"
            or "accesskey"
            or "apikey"
            or "password"
            or "credential"
            or "credentials"
            or "credentialstate"
            or "connectionstring"
            or "clientassertion"
            or "privatekey"
            or "accountkey"
            or "sharedaccesskey"
            or "sharedaccesssignature"
            or "sastoken"
            or "token"
            or "authtoken"
            or "bearertoken"
            or "clientsecret"
            or "secret"
            || normalized.EndsWith("authorization", StringComparison.Ordinal)
            || normalized.EndsWith("authorizationheader", StringComparison.Ordinal)
            || normalized.EndsWith("accesstoken", StringComparison.Ordinal)
            || normalized.EndsWith("refreshtoken", StringComparison.Ordinal)
            || normalized.EndsWith("idtoken", StringComparison.Ordinal)
            || normalized.EndsWith("authtoken", StringComparison.Ordinal)
            || normalized.EndsWith("bearertoken", StringComparison.Ordinal)
            || normalized.EndsWith("apikey", StringComparison.Ordinal)
            || normalized.EndsWith("accesskey", StringComparison.Ordinal)
            || normalized.EndsWith("clientsecret", StringComparison.Ordinal)
            || normalized.EndsWith("credential", StringComparison.Ordinal)
            || normalized.EndsWith("credentials", StringComparison.Ordinal)
            || normalized.EndsWith("credentialstate", StringComparison.Ordinal)
            || normalized.EndsWith("connectionstring", StringComparison.Ordinal)
            || normalized.EndsWith("password", StringComparison.Ordinal)
            || normalized.EndsWith("privatekey", StringComparison.Ordinal)
            || normalized.EndsWith("accountkey", StringComparison.Ordinal)
            || normalized.EndsWith("sharedaccesskey", StringComparison.Ordinal)
            || normalized.EndsWith("sharedaccesssignature", StringComparison.Ordinal)
            || normalized.EndsWith("clientassertion", StringComparison.Ordinal)
            || normalized.EndsWith("secret", StringComparison.Ordinal);
    }

    private static bool IsGuidIdentifierProperty(string? propertyName)
        => propertyName is not null
            && (propertyName.Contains("tenant", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("subscription", StringComparison.OrdinalIgnoreCase));

    private static bool IsActivityIdentifierProperty(string? propertyName)
        => propertyName is not null
            && (propertyName.Contains("trace", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("span", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("parent", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?i)\bbearer\s+(?<token>[a-z0-9\-._~+/]+=*)")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\beyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{6,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\bsk-[a-zA-Z0-9_-]{16,}\b")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"(?i)(?<name>accountkey|sharedaccesssignature|clientsecret|password)\s*=\s*[^;\s]+")]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"(?i)/subscriptions/[0-9a-f-]{36}(?:/resourcegroups/[a-z0-9](?:[a-z0-9._()-]*[a-z0-9_-])?)?(?:/providers/[a-z0-9](?:[a-z0-9._()-]*[a-z0-9_-])?(?:/[a-z0-9](?:[a-z0-9._()-]*[a-z0-9_-])?)+)?")]
    private static partial Regex AzureResourceIdRegex();

    [GeneratedRegex(@"\b(?:resp|msg|item|call|fc|rs)_[a-zA-Z0-9_-]{8,}\b")]
    private static partial Regex OpenAiIdentifierRegex();

    [GeneratedRegex(@"\btool_(?:(?=[a-zA-Z0-9_-]{8,}\b)(?=[a-zA-Z0-9_-]*\d)[a-zA-Z0-9_-]+|[a-zA-Z]{16,})\b")]
    private static partial Regex ToolIdentifierRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{16}|[0-9a-fA-F]{32})\b")]
    private static partial Regex HexIdentifierRegex();
}
