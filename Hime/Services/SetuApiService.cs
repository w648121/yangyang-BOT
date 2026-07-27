using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>Calls the SFW Lolicon search endpoint and the two requested random-image providers.</summary>
public sealed class SetuApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SetuOptions _options;
    private readonly ILogger<SetuApiService> _logger;

    public SetuApiService(
        IHttpClientFactory httpClientFactory,
        IOptions<SetuOptions> options,
        ILogger<SetuApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SetuFetchResult> FetchAsync(
        SetuRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return new SetuFetchResult([], "disabled", "二次元图片功能当前未开启。");

        var count = Math.Clamp(request.Count, 1, Math.Clamp(_options.MaxImagesPerRequest, 1, 20));
        return request.Source == SetuSourceMode.Random
            ? await FetchRandomAsync(count, cancellationToken)
            : await FetchLoliconAsync(count, request.Tags, cancellationToken);
    }

    public async Task<SetuFetchResult> FetchLoliconAsync(
        int count,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, Math.Clamp(_options.MaxImagesPerRequest, 1, 20));
        try
        {
            var tagUri = BuildLoliconUri(_options.LoliconApiUrl, count, tags, useKeyword: false);
            var tagImages = await RequestLoliconAsync(tagUri, cancellationToken);
            if (tagImages.Count > 0 || tags.Count == 0)
                return new SetuFetchResult(tagImages, tags.Count == 0 ? "unfiltered" : "tag");

            // Required fallback order: keyword is attempted only after the tag-filtered
            // request has completed successfully but returned no acceptable data.
            var keywordUri = BuildLoliconUri(_options.LoliconApiUrl, count, tags, useKeyword: true);
            var keywordImages = await RequestLoliconAsync(keywordUri, cancellationToken);
            return new SetuFetchResult(keywordImages, "keyword-fallback");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lolicon image request failed (Count={Count}, Tags={Tags})", count, string.Join(',', tags));
            return new SetuFetchResult([], "lolicon-error", "图片检索服务暂时不可用，请稍后再试。");
        }
    }

    public async Task<SetuFetchResult> FetchRandomAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, Math.Clamp(_options.MaxImagesPerRequest, 1, 20));
        var images = new List<SetuImage>(count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attempts = Math.Max(4, count * 3);

        for (var attempt = 0; attempt < attempts && images.Count < count; attempt++)
        {
            var preferDmoe = Random.Shared.Next(2) == 0;
            var image = preferDmoe
                ? await FetchDmoeOrFallbackAsync(cancellationToken)
                : await FetchLoliOrFallbackAsync(cancellationToken);
            if (image is not null && seen.Add(image.OriginalUrl))
                images.Add(image);
        }

        return images.Count > 0
            ? new SetuFetchResult(images, "random-provider")
            : new SetuFetchResult([], "random-error", "随机图片服务暂时没有返回可用内容，请稍后再试。");
    }

    public Uri BuildLoliconUri(
        string baseUrl,
        int count,
        IReadOnlyList<string> tags,
        bool useKeyword)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("r18", "0"),
            new("num", Math.Clamp(count, 1, 20).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("excludeAI", "true"),
            new("size", "original")
        };

        if (tags.Count > 0)
        {
            if (useKeyword)
            {
                parameters.Add(new KeyValuePair<string, string>("keyword", string.Join(' ', tags)));
            }
            else
            {
                parameters.AddRange(tags.Select(tag =>
                    new KeyValuePair<string, string>(
                        "tag",
                        _options.TagAliases.TryGetValue(tag, out var alias) ? alias : tag)));
            }
        }

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var query = string.Join('&', parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(baseUrl + separator + query, UriKind.Absolute);
    }

    private async Task<IReadOnlyList<SetuImage>> RequestLoliconAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await SendGetAsync(uri, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && !string.IsNullOrWhiteSpace(error.GetString()))
            throw new InvalidOperationException($"Lolicon returned an error: {error.GetString()}");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var images = new List<SetuImage>();
        foreach (var item in data.EnumerateArray())
        {
            var r18 = item.TryGetProperty("r18", out var r18Value) && r18Value.ValueKind == JsonValueKind.True;
            var aiType = item.TryGetProperty("aiType", out var aiValue) && aiValue.TryGetInt32(out var parsedAi)
                ? parsedAi
                : -1;
            if (r18 || aiType != 0)
                continue;
            if (!item.TryGetProperty("urls", out var urls) ||
                !urls.TryGetProperty("original", out var originalValue) ||
                !TryHttpsUrl(originalValue.GetString(), out var original))
            {
                continue;
            }

            var itemTags = item.TryGetProperty("tags", out var tagValues) && tagValues.ValueKind == JsonValueKind.Array
                ? tagValues.EnumerateArray().Select(tag => tag.GetString()).Where(tag => !string.IsNullOrWhiteSpace(tag)).Cast<string>().Take(12).ToArray()
                : [];
            images.Add(new SetuImage(
                original,
                "Lolicon/Pixiv",
                GetString(item, "title"),
                GetString(item, "author"),
                GetInt64(item, "pid"),
                itemTags,
                VerifiedNonAi: true,
                VerifiedNonR18: true));
        }
        return images;
    }

    private async Task<SetuImage?> FetchDmoeOrFallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            var image = await FetchDmoeAsync(cancellationToken);
            if (image is not null)
                return image;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DMOE random image request failed; trying LoliAPI.");
        }
        try
        {
            return await FetchLoliAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LoliAPI fallback request failed.");
            return null;
        }
    }

    private async Task<SetuImage?> FetchLoliOrFallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            var image = await FetchLoliAsync(cancellationToken);
            if (image is not null)
                return image;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LoliAPI random image request failed; trying DMOE.");
        }
        try
        {
            return await FetchDmoeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DMOE fallback request failed.");
            return null;
        }
    }

    private async Task<SetuImage?> FetchDmoeAsync(CancellationToken cancellationToken)
    {
        using var response = await SendGetAsync(new Uri(_options.DmoeApiUrl, UriKind.Absolute), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var value = GetString(root, "source") ?? GetString(root, "imgurl");
        return TryHttpsUrl(value, out var url)
            ? new SetuImage(url, "DMOE")
            : null;
    }

    private async Task<SetuImage?> FetchLoliAsync(CancellationToken cancellationToken)
    {
        using var response = await SendGetAsync(new Uri(_options.LoliApiUrl, UriKind.Absolute), cancellationToken);
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        response.EnsureSuccessStatusCode();
        return TryHttpsUrl(body, out var url)
            ? new SetuImage(url, "LoliAPI")
            : null;
    }

    private async Task<HttpResponseMessage> SendGetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 5, 60)));
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("HimeBot/2026.07");
        try
        {
            return await _httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static bool TryHttpsUrl(string? value, out string url)
    {
        url = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        url = uri.AbsoluteUri;
        return true;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
}
