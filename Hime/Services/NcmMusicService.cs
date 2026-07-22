using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>Searches local ncm-api-rs for song metadata and keeps short-lived pick lists per user.</summary>
public sealed class NcmMusicService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MusicOptions _options;
    private readonly ILogger<NcmMusicService> _logger;
    private readonly ConcurrentDictionary<MusicSelectionKey, PendingSelection> _selections = new();

    public NcmMusicService(
        IHttpClientFactory httpClientFactory,
        IOptions<MusicOptions> options,
        ILogger<NcmMusicService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<IReadOnlyList<MusicSong>> SearchAsync(string keywords, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new MusicSearchException("Music search is disabled.");

        var normalizedKeywords = keywords.Trim();
        if (normalizedKeywords.Length is 0 or > 80)
            return [];

        var limit = Math.Clamp(_options.SearchResultLimit, 1, 10);
        var uri = new Uri(
            new Uri(EnsureTrailingSlash(_options.ApiBaseUrl)),
            $"cloudsearch?keywords={Uri.EscapeDataString(normalizedKeywords)}&limit={limit}&type=1");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 3, 30)));
            using var response = await _httpClientFactory.CreateClient().GetAsync(uri, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new MusicSearchException($"Local music service returned HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return ParseSongs(document.RootElement);
        }
        catch (MusicSearchException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MusicSearchException("The local music service timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Music search failed.");
            throw new MusicSearchException("The local music service is unavailable.");
        }
    }

    public void RememberSelection(long conversationId, long userId, IReadOnlyList<MusicSong> songs)
    {
        if (songs.Count == 0)
            return;

        CleanupExpiredSelections();
        _selections[new MusicSelectionKey(conversationId, userId)] = new PendingSelection(
            DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.SelectionExpiryMinutes, 1, 30)),
            songs.ToArray());
    }

    public bool TryGetSelection(long conversationId, long userId, int index, out MusicSong? song)
    {
        song = null;
        var key = new MusicSelectionKey(conversationId, userId);
        if (!_selections.TryGetValue(key, out var selection))
            return false;
        if (selection.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _selections.TryRemove(key, out _);
            return false;
        }
        if (index < 1 || index > selection.Songs.Length)
            return false;

        song = selection.Songs[index - 1];
        return true;
    }

    private static IReadOnlyList<MusicSong> ParseSongs(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<MusicSong>();
        foreach (var item in songs.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
                continue;

            var title = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var artists = ReadArtists(item);
            var album = item.TryGetProperty("al", out var albumElement) ? ReadString(albumElement, "name") : string.Empty;
            var cover = item.TryGetProperty("al", out albumElement) ? ReadString(albumElement, "picUrl") : string.Empty;
            if (cover.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                cover = "https://" + cover[7..];

            parsed.Add(new MusicSong(
                id,
                title,
                string.IsNullOrWhiteSpace(artists) ? "Unknown artist" : artists,
                string.IsNullOrWhiteSpace(album) ? "Unknown album" : album,
                cover,
                $"https://music.163.com/#/song?id={id}"));
        }

        return parsed;
    }

    private static string ReadArtists(JsonElement song)
    {
        if (!song.TryGetProperty("ar", out var artists) && !song.TryGetProperty("artists", out artists))
            return string.Empty;
        if (artists.ValueKind != JsonValueKind.Array)
            return string.Empty;

        return string.Join('/', artists.EnumerateArray()
            .Select(artist => ReadString(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string ReadString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private void CleanupExpiredSelections()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _selections)
        {
            if (entry.Value.ExpiresAt <= now)
                _selections.TryRemove(entry.Key, out _);
        }
    }

    private static string EnsureTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";

    private sealed record PendingSelection(DateTimeOffset ExpiresAt, MusicSong[] Songs);
    private readonly record struct MusicSelectionKey(long ConversationId, long UserId);
}

public sealed record MusicSong(long Id, string Title, string Artist, string Album, string CoverUrl, string OfficialUrl);

public sealed class MusicSearchException(string message) : Exception(message);
