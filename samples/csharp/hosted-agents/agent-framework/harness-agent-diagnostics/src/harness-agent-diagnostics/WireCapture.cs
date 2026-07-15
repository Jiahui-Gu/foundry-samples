using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessAgentDiagnostics;

internal sealed record WireCaptureSummary(
    HttpStatusCode StatusCode,
    string ContentType,
    int EventCount,
    ImmutableArray<string> EventNames,
    ImmutableArray<string> DataTypes,
    bool Done,
    bool Completed,
    ImmutableArray<string> FailureMarkers,
    ImmutableArray<string> MissingMarkers);

internal sealed class WireCaptureException : Exception
{
    internal WireCaptureException(
        string message,
        HttpStatusCode? statusCode,
        ImmutableArray<string> markers,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Markers = markers;
    }

    internal HttpStatusCode? StatusCode { get; }

    internal ImmutableArray<string> Markers { get; }
}

internal static class WireCapture
{
    internal const string SseFileName = "hosted-responses.sse";
    internal const string EventsFileName = "hosted-responses-events.jsonl";
    private const int FailureExcerptLimit = 4096;
    private const string DiagnosticPrompt =
        """
        Run one local diagnostic only. Do not use web, shell, network, or external files. Do not take risky actions.
        You are explicitly authorized to transition from plan to execute; record mode and todo activity.
        Store exactly this in memory: label=maf-probe; values=3,1,4.
        Make exactly one compute_probe call with [3,1,4], complete the todo, and return compact final JSON.
        """;

    internal static async Task<WireCaptureSummary> CaptureAsync(
        Uri baseUrl,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory must not be blank.", nameof(outputDirectory));
        }

        Uri validatedUrl = LoopbackHttpUrl.Parse(baseUrl.AbsoluteUri);
        string ssePath = Path.Combine(outputDirectory, SseFileName);
        string eventsPath = Path.Combine(outputDirectory, EventsFileName);
        DeleteEvidence(ssePath, eventsPath);

        using SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            Credentials = null,
            PreAuthenticate = false,
            UseCookies = false,
        };
        using HttpClient client = new(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(validatedUrl, "responses"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { input = DiagnosticPrompt, stream = true }),
                Encoding.UTF8,
                "application/json"),
        };

        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!response.IsSuccessStatusCode)
            {
                string excerpt = await ReadSafeExcerptAsync(response.Content, cancellationToken)
                    .ConfigureAwait(false);
                throw new WireCaptureException(
                    $"Responses endpoint returned HTTP {(int)response.StatusCode}. Body excerpt: {excerpt}",
                    response.StatusCode,
                    ["http-status"]);
            }

            if (!mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                string excerpt = await ReadSafeExcerptAsync(response.Content, cancellationToken)
                    .ConfigureAwait(false);
                throw new WireCaptureException(
                    $"Responses endpoint returned an unexpected content type. Body excerpt: {excerpt}",
                    response.StatusCode,
                    ["content-type"]);
            }

            ImmutableArray<SanitizedSseEvent> events;
            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                events = await ReadEventsAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new WireCaptureException(
                    "Responses SSE stream failed before completion.",
                    response.StatusCode,
                    ["stream-failure"],
                    exception);
            }

            WireCaptureSummary summary = CreateSummary(response.StatusCode, mediaType, events);
            await WriteEvidenceAsync(ssePath, eventsPath, events, cancellationToken)
                .ConfigureAwait(false);
            return summary;
        }
        catch
        {
            DeleteEvidence(ssePath, eventsPath);
            throw;
        }
    }

    private static async Task<ImmutableArray<SanitizedSseEvent>> ReadEventsAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        ImmutableArray<SanitizedSseEvent>.Builder events = ImmutableArray.CreateBuilder<SanitizedSseEvent>();
        SensitiveValueSanitizer sanitizer = new([]);
        List<SseField> fields = [];
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (fields.Count > 0)
                {
                    events.Add(SanitizeEvent(events.Count + 1, fields, sanitizer));
                    fields.Clear();
                }

                continue;
            }

            if (line[0] == ':')
            {
                fields.Add(new SseField("comment", TrimSingleLeadingSpace(line[1..])));
                continue;
            }

            int colon = line.IndexOf(':');
            string name = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? string.Empty : TrimSingleLeadingSpace(line[(colon + 1)..]);
            if (name is "event" or "data" or "id" or "retry")
            {
                fields.Add(new SseField(name, value));
            }
        }

        return events.ToImmutable();
    }

    private static SanitizedSseEvent SanitizeEvent(
        int sequence,
        List<SseField> fields,
        SensitiveValueSanitizer sanitizer)
    {
        List<string> dataLines = fields
            .Where(field => field.Name == "data")
            .Select(field => field.Value)
            .ToList();
        string dataText = string.Join('\n', dataLines);
        bool done = dataText == "[DONE]";
        JsonNode? data = null;
        string? failure = null;
        string? dataType = null;
        ImmutableArray<string> renderedDataLines;

        if (done)
        {
            data = JsonValue.Create("[DONE]");
            renderedDataLines = ["[DONE]"];
        }
        else if (dataLines.Count == 0)
        {
            renderedDataLines = [];
        }
        else
        {
            try
            {
                JsonNode? parsed = JsonNode.Parse(dataText);
                data = sanitizer.Sanitize(parsed);
                dataType = (data as JsonObject)?["type"]?.GetValue<string>();
                string sanitizedJson = data?.ToJsonString(JsonOptions) ?? "null";
                renderedDataLines =
                [
                    .. Enumerable
                        .Repeat(string.Empty, dataLines.Count - 1)
                        .Append(sanitizedJson),
                ];
            }
            catch (JsonException)
            {
                failure = "malformed-json";
                data = sanitizer.Sanitize(JsonValue.Create(dataText), "data");
                renderedDataLines =
                [
                    .. dataLines.Select(line => SanitizeText(sanitizer, line, "data")),
                ];
            }
        }

        string eventName = fields
            .Where(field => field.Name == "event")
            .Select(field => field.Value)
            .LastOrDefault() ?? "message";
        string? id = fields
            .Where(field => field.Name == "id")
            .Select(field => field.Value)
            .LastOrDefault();
        string? retry = fields
            .Where(field => field.Name == "retry")
            .Select(field => field.Value)
            .LastOrDefault();
        ImmutableArray<string> comments =
        [
            .. fields
                .Where(field => field.Name == "comment")
                .Select(field => SanitizeText(sanitizer, field.Value, "comment")),
        ];

        return new SanitizedSseEvent(
            sequence,
            SanitizeText(sanitizer, eventName, "event"),
            id is null ? null : SanitizeText(sanitizer, id, "id"),
            retry is null ? null : SanitizeText(sanitizer, retry, "retry"),
            comments,
            dataType,
            data,
            done,
            failure,
            [.. fields],
            renderedDataLines);
    }

    private static WireCaptureSummary CreateSummary(
        HttpStatusCode statusCode,
        string contentType,
        ImmutableArray<SanitizedSseEvent> events)
    {
        bool done = events.Any(item => item.Done);
        bool completed = events.Any(item =>
            item.EventName == "response.completed"
            || item.DataType == "response.completed");
        ImmutableArray<string> failures =
        [
            .. events
                .Select(item => item.Failure)
                .Where(item => item is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal),
        ];
        ImmutableArray<string>.Builder missing = ImmutableArray.CreateBuilder<string>();
        if (!completed)
        {
            missing.Add("missing-response-completed");
        }

        if (!done)
        {
            missing.Add("missing-done");
        }

        return new WireCaptureSummary(
            statusCode,
            contentType,
            events.Length,
            [.. events.Select(item => item.EventName)],
            [.. events.Select(item => item.DataType).Where(item => item is not null).Cast<string>()],
            done,
            completed,
            failures,
            missing.ToImmutable());
    }

    private static async Task WriteEvidenceAsync(
        string ssePath,
        string eventsPath,
        ImmutableArray<SanitizedSseEvent> events,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ssePath)!);
        StringBuilder sse = new();
        StringBuilder jsonl = new();
        foreach (SanitizedSseEvent item in events)
        {
            AppendSseEvent(sse, item);
            JsonObject record = new()
            {
                ["sequence"] = item.Sequence,
                ["event"] = item.EventName,
                ["id"] = item.Id,
                ["retry"] = item.Retry,
                ["comments"] = new JsonArray(
                    item.Comments
                        .Select(comment => (JsonNode?)JsonValue.Create(comment))
                        .ToArray()),
                ["dataType"] = item.DataType,
                ["data"] = item.Data?.DeepClone(),
                ["done"] = item.Done,
                ["failure"] = item.Failure,
            };
            jsonl.AppendLine(record.ToJsonString(JsonOptions));
        }

        await File.WriteAllTextAsync(ssePath, sse.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(eventsPath, jsonl.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AppendSseEvent(StringBuilder output, SanitizedSseEvent item)
    {
        int dataIndex = 0;
        int commentIndex = 0;
        foreach (SseField field in item.OriginalFields)
        {
            switch (field.Name)
            {
                case "comment":
                    output.Append(": ").AppendLine(item.Comments[commentIndex++]);
                    break;
                case "event":
                    output.Append("event: ").AppendLine(item.EventName);
                    break;
                case "id":
                    output.Append("id: ").AppendLine(item.Id);
                    break;
                case "retry":
                    output.Append("retry: ").AppendLine(item.Retry);
                    break;
                case "data":
                    output.Append("data: ")
                        .AppendLine(item.RenderedDataLines[dataIndex++].TrimEnd('\r'));
                    break;
            }
        }

        output.AppendLine();
    }

    private static async Task<string> ReadSafeExcerptAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: false);
        char[] buffer = new char[FailureExcerptLimit];
        int count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        string raw = new(buffer, 0, count);
        SensitiveValueSanitizer sanitizer = new([]);
        return SanitizeText(sanitizer, raw, "data");
    }

    private static string SanitizeText(
        SensitiveValueSanitizer sanitizer,
        string value,
        string propertyName)
        => sanitizer.Sanitize(JsonValue.Create(value), propertyName)?.GetValue<string>()
            ?? string.Empty;

    private static string TrimSingleLeadingSpace(string value)
        => value.StartsWith(' ') ? value[1..] : value;

    private static void DeleteEvidence(params string[] paths)
    {
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private sealed record SseField(string Name, string Value);

    private sealed record SanitizedSseEvent(
        int Sequence,
        string EventName,
        string? Id,
        string? Retry,
        ImmutableArray<string> Comments,
        string? DataType,
        JsonNode? Data,
        bool Done,
        string? Failure,
        ImmutableArray<SseField> OriginalFields,
        ImmutableArray<string> RenderedDataLines);
}
