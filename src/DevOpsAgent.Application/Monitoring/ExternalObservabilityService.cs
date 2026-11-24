using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Llm;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Monitoring;

public sealed record ExternalObservabilitySettings(
    bool PrometheusEnabled,
    string PrometheusBaseUrl,
    bool HasPrometheusToken,
    bool VictoriaLogsEnabled,
    string VictoriaLogsBaseUrl,
    bool HasVictoriaLogsToken);

/// <summary>Blank token fields keep the stored encrypted token.</summary>
public sealed record ExternalObservabilitySettingsInput(
    bool PrometheusEnabled,
    string PrometheusBaseUrl,
    string PrometheusToken,
    bool VictoriaLogsEnabled,
    string VictoriaLogsBaseUrl,
    string VictoriaLogsToken);

public sealed record ObservabilityQueryResult(
    bool Succeeded,
    string Source,
    string Query,
    string Summary,
    string? Error = null);

/// <summary>
/// Optional read-only HTTP client for Prometheus (<c>/api/v1/query</c>) and VictoriaLogs (<c>/select/logsql/query</c>).
/// Enforces <see cref="LlmEndpointLocality.AllowsCredentials"/> before saving or sending credentials.
/// </summary>
public sealed class ExternalObservabilityService(
    IAppSettingRepository settings,
    ISecretProtector protector,
    HttpClient httpClient,
    ILogger<ExternalObservabilityService> logger)
{
    public const string SettingKey = "monitoring.external-observability.v1";
    public const int DefaultLogLimit = 50;
    public const int MaxLogLimit = 200;
    private const int MaxSummaryLength = 12000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExternalObservabilitySettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(cancellationToken);
        return new ExternalObservabilitySettings(
            stored.PrometheusEnabled,
            stored.PrometheusBaseUrl,
            !string.IsNullOrWhiteSpace(stored.ProtectedPrometheusToken),
            stored.VictoriaLogsEnabled,
            stored.VictoriaLogsBaseUrl,
            !string.IsNullOrWhiteSpace(stored.ProtectedVictoriaLogsToken));
    }

    public async Task SaveSettingsAsync(ExternalObservabilitySettingsInput input, CancellationToken cancellationToken)
    {
        ValidateEndpoint(input.PrometheusEnabled, input.PrometheusBaseUrl, "Prometheus");
        ValidateEndpoint(input.VictoriaLogsEnabled, input.VictoriaLogsBaseUrl, "VictoriaLogs");

        var existing = await ReadAsync(cancellationToken);
        var updated = new StoredSettings(
            input.PrometheusEnabled,
            input.PrometheusBaseUrl.Trim().TrimEnd('/'),
            string.IsNullOrWhiteSpace(input.PrometheusToken)
                ? existing.ProtectedPrometheusToken
                : protector.Protect(input.PrometheusToken.Trim()),
            input.VictoriaLogsEnabled,
            input.VictoriaLogsBaseUrl.Trim().TrimEnd('/'),
            string.IsNullOrWhiteSpace(input.VictoriaLogsToken)
                ? existing.ProtectedVictoriaLogsToken
                : protector.Protect(input.VictoriaLogsToken.Trim()));

        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken);
    }

    public async Task<ObservabilityQueryResult> QueryAsync(
        string source,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ObservabilityQueryResult(false, source, string.Empty, string.Empty, "Query must not be empty.");

        return source.Trim().ToLowerInvariant() switch
        {
            "prometheus" => await QueryPrometheusAsync(query.Trim(), cancellationToken),
            "victorialogs" => await QueryVictoriaLogsAsync(query.Trim(), limit, cancellationToken),
            _ => new ObservabilityQueryResult(false, source, query, string.Empty,
                "Unsupported observability source. Choose 'prometheus' or 'victorialogs'.")
        };
    }

    public async Task<ObservabilityQueryResult> QueryPrometheusAsync(string promQl, CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(cancellationToken);
        if (!stored.PrometheusEnabled || string.IsNullOrWhiteSpace(stored.PrometheusBaseUrl))
            return new ObservabilityQueryResult(false, "prometheus", promQl, string.Empty, "Prometheus integration is not enabled.");

        if (!LlmEndpointLocality.AllowsCredentials(stored.PrometheusBaseUrl))
            return new ObservabilityQueryResult(false, "prometheus", promQl, string.Empty,
                "Refused Prometheus query: URL must use HTTPS or target a private/local network address.");

        var requestUri = $"{stored.PrometheusBaseUrl}/api/v1/query?query={Uri.EscapeDataString(promQl)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AttachBearer(request, stored.ProtectedPrometheusToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ObservabilityQueryResult(false, "prometheus", promQl, string.Empty,
                    $"Prometheus returned HTTP {(int)response.StatusCode}.");
            }

            return ParsePrometheusResponse(promQl, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Prometheus query failed ({ErrorType}).", exception.GetType().Name);
            return new ObservabilityQueryResult(false, "prometheus", promQl, string.Empty,
                $"Prometheus query failed ({exception.GetType().Name}).");
        }
    }

    public async Task<ObservabilityQueryResult> QueryVictoriaLogsAsync(string logsQl, int limit, CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(cancellationToken);
        if (!stored.VictoriaLogsEnabled || string.IsNullOrWhiteSpace(stored.VictoriaLogsBaseUrl))
            return new ObservabilityQueryResult(false, "victorialogs", logsQl, string.Empty, "VictoriaLogs integration is not enabled.");

        if (!LlmEndpointLocality.AllowsCredentials(stored.VictoriaLogsBaseUrl))
            return new ObservabilityQueryResult(false, "victorialogs", logsQl, string.Empty,
                "Refused VictoriaLogs query: URL must use HTTPS or target a private/local network address.");

        var boundedLimit = Math.Clamp(limit <= 0 ? DefaultLogLimit : limit, 1, MaxLogLimit);
        var requestUri = $"{stored.VictoriaLogsBaseUrl}/select/logsql/query?query={Uri.EscapeDataString(logsQl)}&limit={boundedLimit.ToString(CultureInfo.InvariantCulture)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        AttachBearer(request, stored.ProtectedVictoriaLogsToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ObservabilityQueryResult(false, "victorialogs", logsQl, string.Empty,
                    $"VictoriaLogs returned HTTP {(int)response.StatusCode}.");
            }

            return ParseVictoriaLogsResponse(logsQl, body, boundedLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("VictoriaLogs query failed ({ErrorType}).", exception.GetType().Name);
            return new ObservabilityQueryResult(false, "victorialogs", logsQl, string.Empty,
                $"VictoriaLogs query failed ({exception.GetType().Name}).");
        }
    }

    public static ObservabilityQueryResult ParsePrometheusResponse(string query, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Prometheus reported an error.";
                return new ObservabilityQueryResult(false, "prometheus", query, string.Empty, error);
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new ObservabilityQueryResult(true, "prometheus", query, "No data returned.");

            var resultType = data.TryGetProperty("resultType", out var typeProp) ? typeProp.GetString() ?? "unknown" : "unknown";
            if (!data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return new ObservabilityQueryResult(true, "prometheus", query, $"resultType={resultType} (empty)");

            var sb = new StringBuilder();
            sb.Append("Prometheus ").Append(resultType).Append(" (").Append(result.GetArrayLength()).AppendLine(" series):");
            var count = 0;
            foreach (var item in result.EnumerateArray())
            {
                if (++count > 50)
                {
                    sb.AppendLine("... (truncated)");
                    break;
                }

                var labels = FormatMetricLabels(item);
                var valueText = FormatMetricValue(item);
                sb.Append("- ").Append(labels).Append(" = ").AppendLine(valueText);
            }

            var summary = sb.ToString().TrimEnd();
            if (summary.Length > MaxSummaryLength) summary = summary[..MaxSummaryLength];
            return new ObservabilityQueryResult(true, "prometheus", query, summary);
        }
        catch (JsonException)
        {
            return new ObservabilityQueryResult(false, "prometheus", query, string.Empty, "Prometheus response was not valid JSON.");
        }
    }

    public static ObservabilityQueryResult ParseVictoriaLogsResponse(string query, string ndjson, int limit)
    {
        var lines = ndjson.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new ObservabilityQueryResult(true, "victorialogs", query, "No matching log entries.");

        var sb = new StringBuilder();
        var shown = 0;
        foreach (var line in lines)
        {
            if (shown >= limit) break;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var time = root.TryGetProperty("_time", out var t) ? t.GetString() : null;
                var stream = root.TryGetProperty("_stream", out var s) ? s.GetString() : null;
                var msg = root.TryGetProperty("_msg", out var m) ? m.GetString() : line;
                sb.Append(time ?? "-");
                if (!string.IsNullOrWhiteSpace(stream)) sb.Append(' ').Append(stream);
                sb.Append(' ').AppendLine(msg);
                shown++;
            }
            catch (JsonException)
            {
                sb.AppendLine(line);
                shown++;
            }
        }

        var summary = sb.ToString().TrimEnd();
        if (summary.Length > MaxSummaryLength) summary = summary[..MaxSummaryLength];
        return new ObservabilityQueryResult(true, "victorialogs", query, summary);
    }

    private static string FormatMetricLabels(JsonElement item)
    {
        if (!item.TryGetProperty("metric", out var metric) || metric.ValueKind != JsonValueKind.Object)
            return "{}";

        var pairs = new List<string>();
        foreach (var prop in metric.EnumerateObject())
        {
            pairs.Add($"{prop.Name}=\"{prop.Value.GetString()}\"");
        }

        return "{" + string.Join(", ", pairs) + "}";
    }

    private static string FormatMetricValue(JsonElement item)
    {
        if (item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2)
            return value[1].ToString();
        if (item.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0)
            return $"{values.GetArrayLength()} point(s)";
        return "n/a";
    }

    private void AttachBearer(HttpRequestMessage request, string? protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken)) return;
        var token = protector.Unprotect(protectedToken);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static void ValidateEndpoint(bool enabled, string baseUrl, string label)
    {
        if (!enabled) return;
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException($"Enter a valid {label} HTTP or HTTPS base URL without embedded credentials or query parameters.");
        }

        if (!LlmEndpointLocality.AllowsCredentials(baseUrl))
        {
            throw new ArgumentException($"Use an HTTPS {label} URL: plain HTTP is only allowed for localhost and private network addresses.");
        }
    }

    private async Task<StoredSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(SettingKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return StoredSettings.Default;
        try
        {
            return JsonSerializer.Deserialize<StoredSettings>(raw, JsonOptions) ?? StoredSettings.Default;
        }
        catch (JsonException exception)
        {
            logger.LogWarning("Stored external observability settings could not be parsed ({ErrorType}); using defaults.",
                exception.GetType().Name);
            return StoredSettings.Default;
        }
    }

    private sealed record StoredSettings(
        bool PrometheusEnabled,
        string PrometheusBaseUrl,
        string? ProtectedPrometheusToken,
        bool VictoriaLogsEnabled,
        string VictoriaLogsBaseUrl,
        string? ProtectedVictoriaLogsToken)
    {
        public static StoredSettings Default { get; } = new(false, string.Empty, null, false, string.Empty, null);
    }
}
