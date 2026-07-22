using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Entities.Events;

namespace Hime.Services;

/// <summary>
/// Sends a standard OneBot music segment through LLBot's loopback-only HTTP endpoint.
/// LLBot signs and renders this as QQ's native music sharing card.
/// </summary>
public sealed class OneBotMusicCardSender
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MusicOptions _options;
    private readonly ILogger<OneBotMusicCardSender> _logger;

    public OneBotMusicCardSender(
        IHttpClientFactory httpClientFactory,
        IOptions<MusicOptions> options,
        ILogger<OneBotMusicCardSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> TrySendAsync(
        MessageReceivedEvent messageEvent,
        MusicSong song,
        CancellationToken cancellationToken = default)
    {
        if (!_options.UseOneBotMusicCard)
            return false;

        var isGroup = messageEvent.Message.SourceType == Sora.Core.Enums.MessageSourceType.Group;
        var endpoint = isGroup ? "send_group_msg" : "send_private_msg";
        var targetId = isGroup
            ? (long)messageEvent.Message.GroupId
            : (long)messageEvent.Message.SenderId;
        if (targetId <= 0)
            return false;

        var segment = new
        {
            type = "music",
            data = new
            {
                type = "163",
                id = song.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        object requestBody = isGroup
            ? new { group_id = targetId, message = new[] { segment } }
            : new { user_id = targetId, message = new[] { segment } };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(EnsureTrailingSlash(_options.OneBotApiBaseUrl), endpoint))
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(_options.OneBotAccessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.OneBotAccessToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 3, 30)));
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
            var responseText = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode || !IsSuccess(responseText))
            {
                _logger.LogWarning(
                    "OneBot music-card request failed (HTTP {StatusCode}, Payload={Payload}).",
                    (int)response.StatusCode,
                    Truncate(responseText, 400));
                return false;
            }

            _logger.LogInformation(
                "Sent native QQ music card through OneBot (Target={Target}, SongId={SongId}).",
                targetId,
                song.Id);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("OneBot music-card request timed out.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OneBot music-card request failed.");
            return false;
        }
    }

    private static bool IsSuccess(string responseText)
    {
        try
        {
            using var response = JsonDocument.Parse(responseText);
            var root = response.RootElement;
            return root.TryGetProperty("status", out var status) &&
                   string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase) &&
                   root.TryGetProperty("retcode", out var retcode) &&
                   retcode.TryGetInt32(out var code) && code == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Uri EnsureTrailingSlash(string baseUrl) =>
        new(baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/", UriKind.Absolute);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
