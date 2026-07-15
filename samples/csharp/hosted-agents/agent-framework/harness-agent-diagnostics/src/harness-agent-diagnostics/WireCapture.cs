using System.Collections.Immutable;
using System.Globalization;
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
    ImmutableArray<string> MissingMarkers)
{
    internal ImmutableArray<string> CleanupWarnings { get; init; } = [];
}

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

internal enum WireCapturePublishCheckpoint
{
    StagingDirectoryCreated,
    PreviousBundleMovedToBackup,
}

internal static class WireCapture
{
    internal const string SseFileName = "hosted-responses.sse";
    internal const string EventsFileName = "hosted-responses-events.jsonl";
    internal const string FailedSseFileName = "hosted-responses.failed.sse";
    internal const string FailedEventsFileName = "hosted-responses-events.failed.jsonl";
    private const int FailureExcerptLimit = 4096;
    private const string DiagnosticPrompt =
        """
        Run one local diagnostic only. Do not use web, shell, network, or external files. Do not take risky actions.
        You are explicitly authorized to transition from plan to execute; record mode and todo activity.
        Store exactly this in memory: label=maf-probe; values=3,1,4.
        Make exactly one compute_probe call with [3,1,4], complete the todo, and return compact final JSON.
        """;
    private const string OwnedArtifactSignature =
        "HarnessAgentDiagnostics.WireCapture.OwnedArtifact/v1";

    /// <summary>
    /// Captures into <paramref name="outputDirectory"/>, which is a dedicated bundle directory
    /// replaced as a whole after both success files are closed and validated.
    /// </summary>
    internal static async Task<WireCaptureSummary> CaptureAsync(
        Uri baseUrl,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => await CaptureAsync(
            baseUrl,
            outputDirectory,
            publishCheckpoint: null,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<WireCaptureSummary> CaptureAsync(
        Uri baseUrl,
        string outputDirectory,
        Action<WireCapturePublishCheckpoint>? publishCheckpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory must not be blank.", nameof(outputDirectory));
        }

        Uri validatedUrl = LoopbackHttpUrl.Parse(baseUrl.AbsoluteUri);
        BundlePaths paths = BundlePaths.Create(outputDirectory);
        Directory.CreateDirectory(paths.Parent);
        RecoverAndCleanOwnedArtifacts(paths);

        using SocketsHttpHandler handler = CreateHttpHandler();
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

        SanitizedCapture capture;
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            capture = await ReadCaptureAsync(stream, cancellationToken).ConfigureAwait(false);
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

        WireCaptureSummary summary = CreateSummary(response.StatusCode, mediaType, capture);
        bool succeeded = summary.FailureMarkers.IsEmpty && summary.MissingMarkers.IsEmpty;
        ImmutableArray<string> cleanupWarnings = await WriteAndPublishEvidenceAsync(
            paths,
            capture,
            summary,
            succeeded,
            publishCheckpoint,
            cancellationToken)
            .ConfigureAwait(false);
        return summary with { CleanupWarnings = cleanupWarnings };
    }

    internal static SocketsHttpHandler CreateHttpHandler()
        => new()
        {
            AllowAutoRedirect = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            UseCookies = false,
            UseProxy = false,
        };

    private static async Task<SanitizedCapture> ReadCaptureAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        CaptureState state = new();
        SensitiveValueSanitizer sanitizer = new([]);
        ImmutableArray<SanitizedSseBlock>.Builder blocks =
            ImmutableArray.CreateBuilder<SanitizedSseBlock>();
        ImmutableArray<SanitizedSseEvent>.Builder events =
            ImmutableArray.CreateBuilder<SanitizedSseEvent>();
        List<SseField> fields = [];

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (fields.Count > 0)
                {
                    ProcessBlock(
                        fields,
                        terminatedByBlankLine: true,
                        sanitizer,
                        state,
                        blocks,
                        events);
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

        if (fields.Count > 0)
        {
            ProcessBlock(
                fields,
                terminatedByBlankLine: false,
                sanitizer,
                state,
                blocks,
                events);
        }

        return new SanitizedCapture(
            blocks.ToImmutable(),
            events.ToImmutable(),
            state.Completed,
            state.Done,
            state.Failures.ToImmutableArray());
    }

    private static void ProcessBlock(
        List<SseField> fields,
        bool terminatedByBlankLine,
        SensitiveValueSanitizer sanitizer,
        CaptureState state,
        ImmutableArray<SanitizedSseBlock>.Builder blocks,
        ImmutableArray<SanitizedSseEvent>.Builder events)
    {
        string? eventValue = null;
        List<string> dataLines = [];
        ImmutableArray<string>.Builder comments = ImmutableArray.CreateBuilder<string>();
        foreach (SseField field in fields)
        {
            switch (field.Name)
            {
                case "comment":
                    comments.Add(SanitizeText(sanitizer, field.Value, "comment"));
                    break;
                case "event":
                    eventValue = field.Value;
                    break;
                case "id" when terminatedByBlankLine && !field.Value.Contains('\0'):
                    state.LastEventId = field.Value;
                    break;
                case "retry" when terminatedByBlankLine && IsValidRetry(field.Value):
                    state.Retry = field.Value;
                    break;
                case "data":
                    dataLines.Add(field.Value);
                    break;
            }
        }

        SanitizedData sanitizedData = SanitizeData(dataLines, sanitizer);
        ImmutableArray<SseField>.Builder sanitizedFields =
            ImmutableArray.CreateBuilder<SseField>(fields.Count);
        int dataIndex = 0;
        foreach (SseField field in fields)
        {
            string value = field.Name switch
            {
                "comment" => SanitizeText(sanitizer, field.Value, "comment"),
                "event" => SanitizeText(sanitizer, field.Value, "event"),
                "id" when field.Value.Contains('\0') => "[IGNORED_NUL_ID]",
                "id" => SanitizeText(sanitizer, field.Value, "id"),
                "retry" => SanitizeText(sanitizer, field.Value, "retry"),
                "data" => sanitizedData.RenderedLines[dataIndex++],
                _ => string.Empty,
            };
            sanitizedFields.Add(new SseField(field.Name, value));
        }

        blocks.Add(new SanitizedSseBlock(
            sanitizedFields.ToImmutable(),
            terminatedByBlankLine));
        if (!terminatedByBlankLine || dataLines.Count == 0)
        {
            return;
        }

        string effectiveEventName = string.IsNullOrEmpty(eventValue) ? "message" : eventValue;
        string sanitizedEventName = SanitizeText(sanitizer, effectiveEventName, "event");
        SanitizedSseEvent capturedEvent = new(
            events.Count + 1,
            sanitizedEventName,
            state.LastEventId is null
                ? null
                : SanitizeText(sanitizer, state.LastEventId, "id"),
            state.Retry,
            comments.ToImmutable(),
            sanitizedData.DataType,
            sanitizedData.Data,
            sanitizedData.Done,
            sanitizedData.Failure);
        events.Add(capturedEvent);

        if (sanitizedData.Failure is not null)
        {
            AddDistinct(state.Failures, sanitizedData.Failure);
        }

        if (state.Done)
        {
            AddDistinct(state.Failures, "content-after-done");
        }

        bool isCompletion =
            effectiveEventName.Equals("response.completed", StringComparison.Ordinal)
            || sanitizedData.DataType?.Equals("response.completed", StringComparison.Ordinal) == true;
        if (sanitizedData.Done)
        {
            if (!state.Completed)
            {
                AddDistinct(state.Failures, "done-before-response-completed");
            }

            state.Done = true;
            return;
        }

        if (state.Done)
        {
            AddDistinct(state.Failures, "data-after-done");
        }

        if (state.Completed)
        {
            AddDistinct(state.Failures, "data-after-response-completed");
        }

        if (isCompletion)
        {
            state.Completed = true;
        }
    }

    private static SanitizedData SanitizeData(
        List<string> dataLines,
        SensitiveValueSanitizer sanitizer)
    {
        if (dataLines.Count == 0)
        {
            return new SanitizedData(null, null, false, null, []);
        }

        string dataText = string.Join('\n', dataLines);
        if (dataText == "[DONE]")
        {
            return new SanitizedData(
                JsonValue.Create("[DONE]"),
                null,
                true,
                null,
                ["[DONE]"]);
        }

        JsonNode? data;
        string? dataType;
        string? failure = null;
        string rendered;
        try
        {
            JsonNode? parsed = JsonNode.Parse(dataText);
            data = sanitizer.Sanitize(parsed);
            dataType = GetStringProperty(data as JsonObject, "type");
            rendered = data?.ToJsonString(JsonOptions) ?? "null";
        }
        catch (JsonException)
        {
            failure = "malformed-json";
            dataType = "unparseable-data";
            data = new JsonObject
            {
                ["kind"] = "parse-failure",
                ["dataType"] = dataType,
                ["length"] = dataText.Length,
                ["redacted"] = true,
            };
            rendered = data.ToJsonString(JsonOptions);
        }

        return new SanitizedData(
            data,
            dataType,
            false,
            failure,
            DistributeRenderedData(dataLines.Count, rendered));
    }

    private static WireCaptureSummary CreateSummary(
        HttpStatusCode statusCode,
        string contentType,
        SanitizedCapture capture)
    {
        ImmutableArray<string>.Builder missing = ImmutableArray.CreateBuilder<string>();
        if (!capture.Completed)
        {
            missing.Add("missing-response-completed");
        }

        return new WireCaptureSummary(
            statusCode,
            contentType,
            capture.Events.Length,
            [.. capture.Events.Select(item => item.EventName)],
            [.. capture.Events.Select(item => item.DataType).Where(item => item is not null).Cast<string>()],
            capture.Done,
            capture.Completed,
            capture.Failures,
            missing.ToImmutable());
    }

    private static async Task<ImmutableArray<string>> WriteAndPublishEvidenceAsync(
        BundlePaths paths,
        SanitizedCapture capture,
        WireCaptureSummary summary,
        bool succeeded,
        Action<WireCapturePublishCheckpoint>? publishCheckpoint,
        CancellationToken cancellationToken)
    {
        string destination = succeeded ? paths.Final : paths.Failed;
        string staging = CreateOwnedArtifactPath(destination, "staging");
        string sseFileName = succeeded ? SseFileName : FailedSseFileName;
        string eventsFileName = succeeded ? EventsFileName : FailedEventsFileName;
        string stagedSsePath = Path.Combine(staging, sseFileName);
        string stagedEventsPath = Path.Combine(staging, eventsFileName);
        (string sse, string jsonl) = RenderEvidence(capture, summary, succeeded);

        try
        {
            Directory.CreateDirectory(staging);
            publishCheckpoint?.Invoke(WireCapturePublishCheckpoint.StagingDirectoryCreated);
            CreateOwnedArtifactMarker(staging);
            await WriteAndFlushAsync(stagedSsePath, sse, cancellationToken)
                .ConfigureAwait(false);
            await WriteAndFlushAsync(stagedEventsPath, jsonl, cancellationToken)
                .ConfigureAwait(false);
            ValidateStagedEvidence(
                staging,
                stagedSsePath,
                stagedEventsPath,
                sse,
                jsonl);
            return PublishBundle(staging, destination, publishCheckpoint);
        }
        finally
        {
            DeleteOwnedArtifact(staging);
        }
    }

    private static void ValidateStagedEvidence(
        string staging,
        string ssePath,
        string eventsPath,
        string expectedSse,
        string expectedJsonl)
    {
        string[] expectedFiles = [Path.GetFileName(eventsPath), Path.GetFileName(ssePath)];
        string[] actualFiles = Directory.EnumerateFiles(staging)
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Array.Sort(expectedFiles, StringComparer.Ordinal);
        if (!actualFiles.SequenceEqual(expectedFiles, StringComparer.Ordinal)
            || File.ReadAllText(ssePath) != expectedSse
            || File.ReadAllText(eventsPath) != expectedJsonl)
        {
            throw new IOException("Staged wire capture evidence failed validation.");
        }

        foreach (string line in File.ReadLines(eventsPath))
        {
            if (line.Length == 0)
            {
                throw new IOException("Staged wire capture JSONL contains an empty record.");
            }

            using JsonDocument document = JsonDocument.Parse(line);
        }
    }

    private static ImmutableArray<string> PublishBundle(
        string staging,
        string destination,
        Action<WireCapturePublishCheckpoint>? publishCheckpoint)
    {
        string backup = CreateOwnedArtifactPath(destination, "backup");
        bool previousBundleMoved = false;

        try
        {
            if (Directory.Exists(destination))
            {
                CreateOwnedArtifactMarker(backup);
                Directory.Move(destination, backup);
                previousBundleMoved = true;
                publishCheckpoint?.Invoke(
                    WireCapturePublishCheckpoint.PreviousBundleMovedToBackup);
            }

            Directory.Move(staging, destination);
        }
        catch (Exception publishException)
        {
            try
            {
                if (previousBundleMoved
                    && Directory.Exists(backup)
                    && !Directory.Exists(destination))
                {
                    Directory.Move(backup, destination);
                }

                DeleteOwnedArtifactMarker(backup);
            }
            catch (Exception recoveryException)
            {
                throw new IOException(
                    "Wire capture bundle publication and recovery failed.",
                    new AggregateException(publishException, recoveryException));
            }

            throw;
        }

        if (!previousBundleMoved)
        {
            return [];
        }

        try
        {
            return DeleteOwnedArtifact(backup)
                ? []
                : ["backup-cleanup-failed"];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ["backup-cleanup-failed"];
        }
    }

    private static (string Sse, string Jsonl) RenderEvidence(
        SanitizedCapture capture,
        WireCaptureSummary summary,
        bool succeeded)
    {
        StringBuilder sse = new();
        foreach (SanitizedSseBlock block in capture.Blocks)
        {
            AppendSseBlock(sse, block);
        }

        StringBuilder jsonl = new();
        foreach (SanitizedSseEvent item in capture.Events)
        {
            JsonObject record = new()
            {
                ["sequence"] = item.Sequence,
                ["event"] = item.EventName,
                ["id"] = item.Id,
                ["retry"] = item.Retry,
                ["comments"] = ToJsonArray(item.Comments),
                ["dataType"] = item.DataType,
                ["data"] = item.Data?.DeepClone(),
                ["done"] = item.Done,
                ["failure"] = item.Failure,
            };
            jsonl.AppendLine(record.ToJsonString(JsonOptions));
        }

        if (!succeeded)
        {
            JsonObject failureSummary = new()
            {
                ["recordType"] = "failure-summary",
                ["failureMarkers"] = ToJsonArray(summary.FailureMarkers),
                ["missingMarkers"] = ToJsonArray(summary.MissingMarkers),
                ["completed"] = summary.Completed,
                ["done"] = summary.Done,
            };
            string renderedSummary = failureSummary.ToJsonString(JsonOptions);
            if (capture.Blocks.IsEmpty || capture.Blocks[^1].TerminatedByBlankLine)
            {
                sse.Append(": capture-failure ").AppendLine(renderedSummary).AppendLine();
            }

            jsonl.AppendLine(renderedSummary);
        }

        return (sse.ToString(), jsonl.ToString());
    }

    private static void AppendSseBlock(StringBuilder output, SanitizedSseBlock block)
    {
        foreach (SseField field in block.Fields)
        {
            if (field.Name == "comment")
            {
                output.Append(": ").AppendLine(field.Value);
            }
            else
            {
                output.Append(field.Name).Append(": ").AppendLine(field.Value);
            }
        }

        if (block.TerminatedByBlankLine)
        {
            output.AppendLine();
        }
    }

    private static async Task WriteAndFlushAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using StreamWriter writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true);
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
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
        char[] buffer = new char[FailureExcerptLimit + 1];
        int count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        bool truncated = count > FailureExcerptLimit;
        int excerptLength = Math.Min(count, FailureExcerptLimit);
        string raw = new(buffer, 0, excerptLength);
        SensitiveValueSanitizer sanitizer = new([]);

        if (!truncated)
        {
            try
            {
                JsonNode? parsed = JsonNode.Parse(raw);
                JsonNode? sanitized = sanitizer.Sanitize(parsed);
                return sanitized?.ToJsonString(JsonOptions) ?? "null";
            }
            catch (JsonException)
            {
            }
        }

        JsonObject marker = new()
        {
            ["bodyType"] = "unparseable-json",
            ["length"] = excerptLength,
            ["truncated"] = truncated,
            ["redacted"] = true,
        };
        return marker.ToJsonString(JsonOptions);
    }

    private static ImmutableArray<string> DistributeRenderedData(int lineCount, string rendered)
    {
        ImmutableArray<string>.Builder lines = ImmutableArray.CreateBuilder<string>(lineCount);
        for (int index = 1; index < lineCount; index++)
        {
            lines.Add(string.Empty);
        }

        lines.Add(rendered);
        return lines.ToImmutable();
    }

    private static bool IsValidRetry(string value)
        => value.Length > 0
            && value.All(character => character is >= '0' and <= '9')
            && long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _);

    private static string? GetStringProperty(JsonObject? value, string propertyName)
        => value?.TryGetPropertyValue(propertyName, out JsonNode? property) == true
            && property is JsonValue scalar
            && scalar.TryGetValue(out string? text)
                ? text
                : null;

    private static JsonArray ToJsonArray(IEnumerable<string> values)
        => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static void AddDistinct(ImmutableArray<string>.Builder values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    private static string SanitizeText(
        SensitiveValueSanitizer sanitizer,
        string value,
        string propertyName)
        => sanitizer.Sanitize(JsonValue.Create(value), propertyName)?.GetValue<string>()
            ?? string.Empty;

    private static string TrimSingleLeadingSpace(string value)
        => value.StartsWith(' ') ? value[1..] : value;

    private static void RecoverAndCleanOwnedArtifacts(BundlePaths paths)
    {
        RecoverOwnedBackup(paths.Final);
        RecoverOwnedBackup(paths.Failed);
        DeleteOwnedArtifacts(paths.Final, "staging");
        DeleteOwnedArtifacts(paths.Failed, "staging");
    }

    private static void RecoverOwnedBackup(string destination)
    {
        List<OwnedArtifact> backups = EnumerateOwnedArtifacts(destination, "backup")
            .OrderByDescending(artifact => File.GetLastWriteTimeUtc(artifact.Marker))
            .ToList();
        if (!Directory.Exists(destination))
        {
            OwnedArtifact? backup = backups.FirstOrDefault(
                artifact => Directory.Exists(artifact.Path));
            if (backup is not null)
            {
                Directory.Move(backup.Path, destination);
                DeleteOwnedArtifactMarker(backup.Path);
                backups.Remove(backup);
            }
        }

        foreach (OwnedArtifact backup in backups)
        {
            DeleteOwnedArtifact(backup.Path);
        }
    }

    private static void DeleteOwnedArtifacts(string destination, string kind)
    {
        foreach (OwnedArtifact artifact in EnumerateOwnedArtifacts(destination, kind))
        {
            DeleteOwnedArtifact(artifact.Path);
        }
    }

    private static IEnumerable<OwnedArtifact> EnumerateOwnedArtifacts(
        string destination,
        string kind)
    {
        string parent = Path.GetDirectoryName(destination)!;
        string prefix = Path.GetFileName(destination) + "." + kind + ".";
        foreach (string marker in Directory.EnumerateFiles(parent, prefix + "*.owned"))
        {
            string markerName = Path.GetFileName(marker);
            const string markerSuffix = ".owned";
            if (markerName.Length != prefix.Length + 32 + markerSuffix.Length
                || !markerName.StartsWith(prefix, StringComparison.Ordinal)
                || !markerName.EndsWith(markerSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string identifier = markerName.Substring(prefix.Length, 32);
            if (!Guid.TryParseExact(identifier, "N", out _))
            {
                continue;
            }

            string artifactPath = marker[..^markerSuffix.Length];
            string expectedMarker =
                OwnedArtifactSignature + Environment.NewLine + Path.GetFullPath(artifactPath);
            string actualMarker;
            try
            {
                actualMarker = File.ReadAllText(marker);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (actualMarker.Equals(expectedMarker, StringComparison.Ordinal))
            {
                yield return new OwnedArtifact(artifactPath, marker);
            }
        }
    }

    private static string CreateOwnedArtifactPath(string destination, string kind)
        => destination + "." + kind + "." + Guid.NewGuid().ToString("N");

    private static void CreateOwnedArtifactMarker(string artifactPath)
    {
        string markerPath = OwnedArtifactMarker(artifactPath);
        using FileStream stream = new(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024,
            FileOptions.WriteThrough);
        using StreamWriter writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);
        writer.Write(
            OwnedArtifactSignature + Environment.NewLine + Path.GetFullPath(artifactPath));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string OwnedArtifactMarker(string artifactPath)
        => artifactPath + ".owned";

    private static bool DeleteOwnedArtifact(string artifactPath)
    {
        if (Directory.Exists(artifactPath))
        {
            Directory.Delete(artifactPath, recursive: true);
        }

        return DeleteOwnedArtifactMarker(artifactPath);
    }

    private static bool DeleteOwnedArtifactMarker(string artifactPath)
    {
        string marker = OwnedArtifactMarker(artifactPath);
        try
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private sealed class CaptureState
    {
        internal string? LastEventId { get; set; }

        internal string? Retry { get; set; }

        internal bool Completed { get; set; }

        internal bool Done { get; set; }

        internal ImmutableArray<string>.Builder Failures { get; } =
            ImmutableArray.CreateBuilder<string>();
    }

    private sealed record BundlePaths(
        string Parent,
        string Final,
        string Failed)
    {
        internal static BundlePaths Create(string directory)
        {
            string final = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string? parent = Path.GetDirectoryName(final);
            if (parent is null || Path.GetFileName(final).Length == 0)
            {
                throw new ArgumentException(
                    "Output directory must be a dedicated capture bundle with a parent directory.",
                    nameof(directory));
            }

            return new BundlePaths(parent, final, final + ".failed");
        }
    }

    private sealed record OwnedArtifact(string Path, string Marker);

    private sealed record SseField(string Name, string Value);

    private sealed record SanitizedSseBlock(
        ImmutableArray<SseField> Fields,
        bool TerminatedByBlankLine);

    private sealed record SanitizedSseEvent(
        int Sequence,
        string EventName,
        string? Id,
        string? Retry,
        ImmutableArray<string> Comments,
        string? DataType,
        JsonNode? Data,
        bool Done,
        string? Failure);

    private sealed record SanitizedData(
        JsonNode? Data,
        string? DataType,
        bool Done,
        string? Failure,
        ImmutableArray<string> RenderedLines);

    private sealed record SanitizedCapture(
        ImmutableArray<SanitizedSseBlock> Blocks,
        ImmutableArray<SanitizedSseEvent> Events,
        bool Completed,
        bool Done,
        ImmutableArray<string> Failures);
}
