using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hime.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hime.Services;

/// <summary>OneBot V11 HTTP 调用的单一出口，供点歌、合并转发等模块复用。</summary>
public sealed class OneBotApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OneBotOptions _options;
    private readonly BotAccountsOptions _accounts;
    private readonly ILogger<OneBotApiClient> _logger;

    public OneBotApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<OneBotOptions> options,
        IOptions<BotAccountsOptions> accounts,
        ILogger<OneBotApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _accounts = accounts.Value;
        _logger = logger;
    }

    public async Task<OneBotCallResult> PostAsync(
        string action,
        object payload,
        long? selfId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var account = selfId is > 0
                ? _accounts.GetEnabledConnections().FirstOrDefault(candidate => candidate.SelfId == selfId)
                : null;
            var baseUrl = !string.IsNullOrWhiteSpace(account?.OneBotApiBaseUrl)
                ? account.OneBotApiBaseUrl
                : _options.ApiBaseUrl;
            var accessToken = !string.IsNullOrWhiteSpace(account?.AccessToken)
                ? account.AccessToken
                : _options.AccessToken;
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(EnsureTrailingSlash(baseUrl), action))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(accessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 3, 90)));
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
            var responseText = await response.Content.ReadAsStringAsync(timeout.Token);
            var success = response.IsSuccessStatusCode && IsSuccess(responseText);
            if (!success)
            {
                _logger.LogWarning(
                    "OneBot action {Action} failed (HTTP {StatusCode}, Payload={Payload}).",
                    action,
                    (int)response.StatusCode,
                    Truncate(responseText, 500));
            }

            return new OneBotCallResult(success, (int)response.StatusCode, responseText);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("OneBot action {Action} timed out.", action);
            return new OneBotCallResult(false, 0, "timeout");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OneBot action {Action} failed.", action);
            return new OneBotCallResult(false, 0, ex.Message);
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

public sealed record OneBotCallResult(bool Success, int StatusCode, string ResponseText);
