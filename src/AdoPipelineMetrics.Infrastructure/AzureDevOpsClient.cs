using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdoPipelineMetrics.Application;
using AdoPipelineMetrics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdoPipelineMetrics.Infrastructure;

public sealed class AzureDevOpsClient : IAzureDevOpsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ArtifactSizeKeys = ["size", "fileSize", "artifactsize", "artifactSize", "contentLength", "Content-Length"];

    private readonly HttpClient _httpClient;
    private readonly AzureDevOpsOptions _options;
    private readonly ILogger<AzureDevOpsClient> _logger;

    public AzureDevOpsClient(HttpClient httpClient, IOptions<AzureDevOpsOptions> options, ILogger<AzureDevOpsClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Authorization = CreateAuthorizationHeader();
    }

    public async Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"_apis/git/repositories?api-version=7.1", cancellationToken);
        return document.RootElement.GetPropertyOrEmptyArray("value")
            .Select(repository => new RepositoryInfo(
                repository.GetStringOrDefault("id"),
                repository.GetStringOrDefault("name")))
            .Where(static repository => !string.IsNullOrWhiteSpace(repository.Id) && !string.IsNullOrWhiteSpace(repository.Name))
            .ToArray();
    }

    public async Task<IReadOnlyList<PipelineDefinitionInfo>> GetDefinitionsByRepositoryAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var query = $"_apis/build/definitions?repositoryId={Uri.EscapeDataString(repositoryId)}&repositoryType=TfsGit&api-version=7.1";
        return await GetDefinitionsFromQueryAsync(query, cancellationToken);
    }

    public Task<IReadOnlyList<PipelineDefinitionInfo>> GetAllDefinitionsAsync(CancellationToken cancellationToken)
    {
        return GetDefinitionsFromQueryAsync("_apis/build/definitions?api-version=7.1", cancellationToken);
    }

    public async Task<IReadOnlyList<BuildRunInfo>> GetBuildsAsync(int definitionId, string repositoryId, string branchName, int topBuilds, CancellationToken cancellationToken)
    {
        var query = new StringBuilder("_apis/build/builds?");
        query.Append(CultureInfo.InvariantCulture, $"definitions={definitionId}");
        query.Append("&repositoryId=").Append(Uri.EscapeDataString(repositoryId));
        query.Append("&repositoryType=TfsGit");
        query.Append("&branchName=").Append(Uri.EscapeDataString(branchName));
        query.Append(CultureInfo.InvariantCulture, $"&$top={topBuilds}");
        query.Append("&queryOrder=finishTimeDescending&api-version=7.1");

        using var document = await GetJsonAsync(query.ToString(), cancellationToken);
        return document.RootElement.GetPropertyOrEmptyArray("value")
            .Select(build => new BuildRunInfo(
                build.GetIntOrDefault("id"),
                build.GetStringOrDefault("buildNumber"),
                build.GetPropertyOrNull("definition")?.GetIntOrDefault("id") ?? definitionId,
                build.GetPropertyOrNull("definition")?.GetStringOrDefault("name") ?? string.Empty,
                build.GetPropertyOrNull("repository")?.GetStringOrDefault("id"),
                build.GetPropertyOrNull("repository")?.GetStringOrDefault("name"),
                build.GetStringOrDefault("sourceBranch"),
                build.GetStringOrDefault("sourceVersion"),
                build.GetStringOrDefault("reason"),
                build.GetStringOrDefault("status"),
                build.GetStringOrDefault("result"),
                build.GetDateTimeOffsetOrNull("queueTime"),
                build.GetDateTimeOffsetOrNull("startTime"),
                build.GetDateTimeOffsetOrNull("finishTime")))
            .Where(static build => build.Id > 0)
            .ToArray();
    }

    public async Task<IReadOnlyList<TimelineRecord>> GetTimelineRecordsAsync(int buildId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"_apis/build/builds/{buildId}/timeline?api-version=7.1", cancellationToken);
        return document.RootElement.GetPropertyOrEmptyArray("records")
            .Select(record => new TimelineRecord(
                record.GetStringOrDefault("id"),
                record.GetStringOrNull("parentId"),
                record.GetStringOrDefault("type"),
                record.GetStringOrDefault("name"),
                record.GetDateTimeOffsetOrNull("startTime"),
                record.GetDateTimeOffsetOrNull("finishTime"),
                record.GetStringOrNull("result"),
                record.GetStringOrNull("state"),
                record.GetIntOrDefault("errorCount"),
                record.GetIntOrDefault("warningCount"),
                record.GetPropertyOrNull("log")?.GetIntOrDefault("id"),
                record.GetPropertyOrNull("worker")?.GetStringOrNull("name") ?? record.GetStringOrNull("workerName"),
                record.GetPropertyOrNull("task") is { } task
                    ? new TimelineTaskReference(task.GetStringOrNull("id"), task.GetStringOrNull("name"))
                    : null))
            .Where(static record => !string.IsNullOrWhiteSpace(record.Id))
            .ToArray();
    }

    public async Task<IReadOnlyList<BuildArtifactInfo>> GetArtifactsAsync(int buildId, bool downloadArtifactsForSize, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"_apis/build/builds/{buildId}/artifacts?api-version=7.1", cancellationToken);
        var artifacts = new List<BuildArtifactInfo>();
        foreach (var artifact in document.RootElement.GetPropertyOrEmptyArray("value"))
        {
            var resource = artifact.GetPropertyOrNull("resource");
            var properties = resource?.GetPropertyOrNull("properties")?.ToDictionary() ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var downloadUrl = resource?.GetStringOrNull("downloadUrl");
            var size = TryGetSizeFromProperties(properties);
            var status = size.HasValue ? "FromProperties" : "UnavailableFromApi";

            if (!size.HasValue && !string.IsNullOrWhiteSpace(downloadUrl))
            {
                size = await TryGetContentLengthAsync(downloadUrl, cancellationToken);
                status = size.HasValue ? "FromHeadContentLength" : "UnavailableFromApi";
            }

            if (!size.HasValue && downloadArtifactsForSize && !string.IsNullOrWhiteSpace(downloadUrl))
            {
                size = await TryCountDownloadBytesAsync(downloadUrl, cancellationToken);
                status = size.HasValue ? "FromStreamDownload" : "UnavailableFromApi";
            }

            artifacts.Add(new BuildArtifactInfo(
                artifact.GetStringOrDefault("name"),
                resource?.GetStringOrNull("type"),
                size,
                size.HasValue ? Math.Round(size.Value / 1024d / 1024d, 4) : null,
                status,
                downloadUrl,
                resource?.GetStringOrNull("data"),
                properties));
        }

        return artifacts;
    }

    private async Task<IReadOnlyList<PipelineDefinitionInfo>> GetDefinitionsFromQueryAsync(string query, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(query, cancellationToken);
        return document.RootElement.GetPropertyOrEmptyArray("value")
            .Select(definition =>
            {
                var repository = definition.GetPropertyOrNull("repository");
                return new PipelineDefinitionInfo(
                    definition.GetIntOrDefault("id"),
                    definition.GetStringOrDefault("name"),
                    repository?.GetStringOrNull("id"),
                    repository?.GetStringOrNull("name"));
            })
            .Where(static definition => definition.Id > 0)
            .ToArray();
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Get, BuildUri(relativePath)), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var clone = await CloneRequestAsync(request, cancellationToken);
            var response = await _httpClient.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!ShouldRetry(response.StatusCode) || attempt == 3)
            {
                return response;
            }

            _logger.LogWarning("Azure DevOps request returned {StatusCode}; retrying attempt {Attempt}.", (int)response.StatusCode, attempt + 1);
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
        }

        throw new InvalidOperationException("Retry loop ended unexpectedly.");
    }

    private async Task<long?> TryGetContentLengthAsync(string downloadUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Head, downloadUrl), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.Headers.ContentLength;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not read artifact Content-Length with HEAD.");
            return null;
        }
    }

    private async Task<long?> TryCountDownloadBytesAsync(string downloadUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Get, downloadUrl), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
            }

            return total;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not count artifact bytes by stream download.");
            return null;
        }
    }

    private AuthenticationHeaderValue CreateAuthorizationHeader()
    {
        var pat = !string.IsNullOrWhiteSpace(_options.PersonalAccessTokenEnvironmentVariable)
            ? Environment.GetEnvironmentVariable(_options.PersonalAccessTokenEnvironmentVariable)
            : null;

        pat = string.IsNullOrWhiteSpace(pat) ? _options.PersonalAccessToken : pat;
        if (string.IsNullOrWhiteSpace(pat))
        {
            throw new InvalidOperationException("Azure DevOps PAT is required. Configure AzureDevOps:PersonalAccessTokenEnvironmentVariable or AzureDevOps:PersonalAccessToken.");
        }

        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
    }

    private Uri BuildUri(string relativePath)
    {
        return new Uri($"https://dev.azure.com/{Uri.EscapeDataString(_options.Organization)}/{Uri.EscapeDataString(_options.Project)}/{relativePath}");
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    }

    private static long? TryGetSizeFromProperties(IReadOnlyDictionary<string, string?> properties)
    {
        foreach (var key in ArtifactSizeKeys)
        {
            if (properties.TryGetValue(key, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                return size;
            }
        }

        return null;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var memory = new MemoryStream();
            await request.Content.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            clone.Content = new StreamContent(memory);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}

internal static class JsonElementExtensions
{
    public static IEnumerable<JsonElement> GetPropertyOrEmptyArray(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
            : [];
    }

    public static JsonElement? GetPropertyOrNull(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? property
            : null;
    }

    public static string GetStringOrDefault(this JsonElement element, string propertyName)
    {
        return element.GetStringOrNull(propertyName) ?? string.Empty;
    }

    public static string? GetStringOrNull(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    public static int GetIntOrDefault(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };
    }

    public static DateTimeOffset? GetDateTimeOffsetOrNull(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String && property.TryGetDateTimeOffset(out var value)
            ? value
            : null;
    }

    public static Dictionary<string, string?> ToDictionary(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        return element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
    }
}
