using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;
using Sora.Entities.Segments;

namespace Hime.Services;

/// <summary>
/// Converts an explicit Hime command into GsCore's loopback HTTP protocol and
/// converts the returned segments back to Sora/LLBot messages.
/// </summary>
public sealed class GsCoreBridgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly ILogger<GsCoreBridgeService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GsCoreServerService _server;
    private readonly GsCoreOptions _options;
    private readonly AdminOptions _admin;

    public GsCoreBridgeService(
        ILogger<GsCoreBridgeService> logger,
        IHttpClientFactory httpClientFactory,
        GsCoreServerService server,
        IOptions<GsCoreOptions> options,
        IOptions<AdminOptions> admin)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _server = server;
        _options = options.Value;
        _admin = admin.Value;
    }

    public bool ShouldHandle(string? text) =>
        _options.Enabled && TryNormalizeCommand(text, out _);

    public async Task HandleAsync(MessageReceivedEvent e, string? rawText, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeCommand(rawText, out var command))
            return;

        try
        {
            if (!await _server.EnsureAvailableAsync(cancellationToken))
            {
                await ReplyTextAsync(e, "鸣潮功能服务暂时没有启动，请稍后再试。", cancellationToken);
                return;
            }

            var payload = BuildRequest(e, command);
            var endpoint = new Uri(new Uri(_options.BaseUrl), "api/send_msg");
            var client = _httpClientFactory.CreateClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 5, 120)));

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GsCore returned HTTP {StatusCode} for command {Command}.",
                    (int)response.StatusCode, command);
                await ReplyTextAsync(e, "鸣潮功能服务请求失败，请稍后再试。", cancellationToken);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = document.RootElement;
            var status = root.TryGetProperty("status_code", out var statusElement) && statusElement.TryGetInt32(out var value)
                ? value
                : -1;

            if (status == -100 || !root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            {
                await ReplyTextAsync(e, "没有匹配到这个鸣潮指令，发送 ww帮助 查看用法。", cancellationToken);
                return;
            }

            if (status != 200 || !data.TryGetProperty("content", out var segments) || segments.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("GsCore returned an unexpected response for command {Command}: status={Status}.", command, status);
                await ReplyTextAsync(e, "鸣潮功能服务返回了无法识别的结果。", cancellationToken);
                return;
            }

            await SendSegmentsAsync(e, segments, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ReplyTextAsync(e, "鸣潮功能服务处理超时，请稍后再试。", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GsCore bridge failed for command {Command}.", command);
            await ReplyTextAsync(e, "鸣潮功能服务处理失败，错误已经记录。", cancellationToken);
        }
    }

    private object BuildRequest(MessageReceivedEvent e, string command)
    {
        var isGroup = e.Message.SourceType == MessageSourceType.Group;
        var senderId = e.Sender?.UserId ?? e.Message.SenderId;
        var senderName = e.Sender?.Nickname ?? e.Member?.Nickname ?? senderId.ToString();
        var segments = new List<object>
        {
            new { type = "text", data = command }
        };

        if (e.Message.Body is not null)
        {
            foreach (var mention in e.Message.Body.OfType<MentionSegment>())
            {
                if (mention.Target != e.SelfId)
                    segments.Add(new { type = "at", data = mention.Target.ToString() });
            }

            foreach (var image in e.Message.Body.OfType<ImageSegment>())
            {
                var source = image.Url?.ToString();
                if (string.IsNullOrWhiteSpace(source))
                    source = image.FileUri?.ToString();
                if (!string.IsNullOrWhiteSpace(source))
                    segments.Add(new { type = "image", data = source });
            }

            foreach (var reply in e.Message.Body.OfType<ReplySegment>())
                segments.Add(new { type = "reply", data = reply.TargetId.ToString() });
        }

        return new
        {
            bot_id = _options.BotId,
            bot_self_id = e.SelfId.ToString(),
            msg_id = e.Message.MessageId.ToString(),
            user_type = isGroup ? "group" : "direct",
            group_id = isGroup ? e.Message.GroupId.ToString() : null,
            user_id = senderId.ToString(),
            sender = new Dictionary<string, object?>
            {
                ["user_id"] = senderId.ToString(),
                ["nickname"] = senderName
            },
            user_pm = _admin.AllowedUserIds.Contains(senderId) ? 1 : 3,
            content = segments
        };
    }

    private async Task SendSegmentsAsync(
        MessageReceivedEvent e,
        JsonElement segments,
        CancellationToken cancellationToken)
    {
        var message = new MessageBody();

        async Task FlushAsync()
        {
            if (message.Count == 0)
                return;
            await SendMessageAsync(e, message, cancellationToken);
            message = new MessageBody();
        }

        async Task VisitAsync(JsonElement segmentArray)
        {
            foreach (var segment in segmentArray.EnumerateArray())
            {
                if (!segment.TryGetProperty("type", out var typeElement))
                    continue;
                var type = typeElement.GetString()?.ToLowerInvariant();
                segment.TryGetProperty("data", out var data);

                switch (type)
                {
                    case "text":
                    case "markdown":
                        var text = GetString(data);
                        if (!string.IsNullOrEmpty(text))
                            message.AddText(text);
                        break;
                    case "at":
                        var target = GetString(data);
                        if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
                            message.AddMentionAll();
                        else if (long.TryParse(target, out var userId))
                            message.AddMention(userId);
                        break;
                    case "reply":
                        if (long.TryParse(GetString(data), out var messageId))
                            message.AddReply(messageId);
                        break;
                    case "image":
                        var imageUri = await ResolveMediaUriAsync(GetString(data), "image", cancellationToken);
                        if (imageUri is not null)
                            message.AddImage(imageUri, ImageSubType.Normal);
                        break;
                    case "node" when data.ValueKind == JsonValueKind.Array:
                        await VisitAsync(data);
                        break;
                    case "record":
                        await FlushAsync();
                        var audioUri = await ResolveMediaUriAsync(GetString(data), "audio", cancellationToken);
                        if (audioUri is not null)
                            await SendMessageAsync(e, new MessageBody().AddAudio(audioUri), cancellationToken);
                        break;
                    case "video":
                        await FlushAsync();
                        var videoUri = await ResolveMediaUriAsync(GetString(data), "video", cancellationToken);
                        if (videoUri is not null)
                            await SendMessageAsync(e, new MessageBody().AddVideo(videoUri, string.Empty), cancellationToken);
                        break;
                    case "file":
                        await FlushAsync();
                        await SendFileAsync(e, GetString(data), cancellationToken);
                        break;
                    case "image_size":
                    case "buttons":
                    case "template_buttons":
                    case "template_markdown":
                    case "group":
                    case "direct":
                        break;
                    default:
                        if (!string.IsNullOrWhiteSpace(type) && !type.StartsWith("log_", StringComparison.OrdinalIgnoreCase))
                            _logger.LogDebug("Ignoring unsupported GsCore segment type {Type}.", type);
                        break;
                }
            }
        }

        await VisitAsync(segments);
        await FlushAsync();
    }

    private async Task<string?> ResolveMediaUriAsync(
        string? data,
        string kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        if (data.StartsWith("link://", StringComparison.OrdinalIgnoreCase))
            return data[7..];
        if (Uri.TryCreate(data, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile))
            return data;

        var encoded = data.StartsWith("base64://", StringComparison.OrdinalIgnoreCase) ? data[9..] : data;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            _logger.LogWarning("GsCore returned invalid base64 {Kind} data.", kind);
            return null;
        }

        var maxBytes = Math.Max(1, _options.MaxMediaMegabytes) * 1024L * 1024L;
        if (bytes.LongLength > maxBytes)
        {
            _logger.LogWarning("GsCore {Kind} output exceeded the {Limit} MB limit.", kind, _options.MaxMediaMegabytes);
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var extension = DetectExtension(bytes, kind);
        var directory = Path.IsPathRooted(_options.OutputDirectory)
            ? _options.OutputDirectory
            : Path.Combine(AppContext.BaseDirectory, _options.OutputDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{hash}{extension}");
        if (!File.Exists(path))
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    private async Task SendFileAsync(MessageReceivedEvent e, string? data, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(data))
            return;
        var separator = data.IndexOf('|');
        if (separator <= 0 || separator == data.Length - 1)
            return;

        var fileName = Path.GetFileName(data[..separator]);
        var source = data[(separator + 1)..];
        string fileUri;
        if (source.StartsWith("link://", StringComparison.OrdinalIgnoreCase))
            fileUri = source[7..];
        else if (source.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
            fileUri = source;
        else
            fileUri = $"base64://{source}";

        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.UploadGroupFileAsync(e.Message.GroupId, fileUri, fileName, "/", cancellationToken);
        else
            await e.Api.UploadPrivateFileAsync(e.Message.SenderId, fileUri, fileName, cancellationToken);
    }

    private static async Task SendMessageAsync(
        MessageReceivedEvent e,
        MessageBody message,
        CancellationToken cancellationToken)
    {
        if (e.Message.SourceType == MessageSourceType.Group)
            await e.Api.SendGroupMessageAsync(e.Message.GroupId, message, cancellationToken);
        else
            await e.Api.SendFriendMessageAsync(e.Message.SenderId, message, cancellationToken);
    }

    private static Task ReplyTextAsync(
        MessageReceivedEvent e,
        string text,
        CancellationToken cancellationToken) =>
        SendMessageAsync(e, new MessageBody(text), cancellationToken);

    private bool TryNormalizeCommand(string? text, out string command)
    {
        command = string.Empty;
        var trimmed = text?.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        foreach (var prefix in _options.CommandPrefixes
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .OrderByDescending(p => p.Length))
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (trimmed.Length > prefix.Length && IsAsciiWordCharacter(trimmed[prefix.Length]))
                return false;

            var suffix = trimmed[prefix.Length..];
            command = prefix.StartsWith('/') ? $"ww{suffix}" : trimmed;
            return true;
        }

        return false;
    }

    private static bool IsAsciiWordCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    private static string? GetString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        _ => null
    };

    private static string DetectExtension(ReadOnlySpan<byte> bytes, string kind)
    {
        if (bytes.StartsWith("\x89PNG"u8)) return ".png";
        if (bytes.StartsWith("GIF8"u8)) return ".gif";
        if (bytes.StartsWith("\xFF\xD8\xFF"u8)) return ".jpg";
        if (bytes.Length >= 12 && bytes[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        if (bytes.StartsWith("OggS"u8)) return ".ogg";
        if (bytes.StartsWith("RIFF"u8)) return ".wav";
        if (bytes.StartsWith("ID3"u8)) return ".mp3";
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8)) return ".mp4";
        return kind switch
        {
            "audio" => ".wav",
            "video" => ".mp4",
            _ => ".bin"
        };
    }
}
