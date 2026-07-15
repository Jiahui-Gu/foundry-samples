using System.Collections;
using System.Reflection;
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

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.Ordinal);
    private readonly SensitiveValueSanitizer _sanitizer;
    private bool _disposed;

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

    public Task RecordAgentResponseUpdateAsync(object update, CancellationToken cancellationToken = default)
        => WriteAsync(AgentResponseUpdatesFileName, update, cancellationToken);

    public Task RecordAgentResponseUpdateAsync(AgentResponseUpdate update, CancellationToken cancellationToken = default)
        => WriteAsync(AgentResponseUpdatesFileName, ContentProjection.Project(update), cancellationToken);

    public Task RecordProviderStateAsync(object state, CancellationToken cancellationToken = default)
        => WriteAsync(ProviderStateFileName, state, cancellationToken);

    public Task RecordActivityAsync(object activity, CancellationToken cancellationToken = default)
        => WriteAsync(ActivitiesFileName, activity, cancellationToken);

    public Task RecordActivityAsync(ActivitySnapshot activity, CancellationToken cancellationToken = default)
        => WriteAsync(ActivitiesFileName, ProjectActivity(activity), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _writeLock.WaitAsync().ConfigureAwait(false);
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
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }

    private async Task WriteAsync(string fileName, object value, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            JsonNode? node = CreateSafeNode(value);
            JsonNode sanitizedNode = _sanitizer.Sanitize(node) ?? JsonValue.Create((string?)null)!;
            string json = sanitizedNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            StreamWriter writer = GetWriter(fileName);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception)
            {
                continue;
            }

            properties[property.Name] = CreateSafeNode(propertyValue, depth + 1);
        }

        return properties;
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
                if (IsForbiddenProperty(name))
                {
                    obj.Remove(name);
                    continue;
                }

                obj[name] = Sanitize(child, name);
            }

            return obj;
        }

        if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                array[index] = Sanitize(array[index], propertyName);
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

        sanitized = BearerTokenRegex().Replace(
            sanitized,
            match => string.Concat("Bearer", " ", GetAlias("token", match.Groups["token"].Value)));
        sanitized = AzureResourceIdRegex().Replace(sanitized, match => GetAlias("azure-resource", match.Value));
        sanitized = OpenAiIdentifierRegex().Replace(sanitized, match => GetAlias(GetOpenAiIdentifierCategory(match.Value), match.Value));

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

    private static bool IsForbiddenProperty(string name)
        => name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("authorizationHeader", StringComparison.OrdinalIgnoreCase)
            || name.Contains("header", StringComparison.OrdinalIgnoreCase)
            || name.Equals("stackTrace", StringComparison.OrdinalIgnoreCase)
            || name.Equals("exception", StringComparison.OrdinalIgnoreCase);

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

    [GeneratedRegex(@"(?i)/subscriptions/[0-9a-f-]{36}(?:/resourcegroups/[^/\s]+)?/providers/[^/\s]+(?:/[^?\s]+)*")]
    private static partial Regex AzureResourceIdRegex();

    [GeneratedRegex(@"\b(?:resp|msg|item|call|fc|rs)_[a-zA-Z0-9_-]{8,}\b")]
    private static partial Regex OpenAiIdentifierRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{16}|[0-9a-fA-F]{32})\b")]
    private static partial Regex HexIdentifierRegex();
}
