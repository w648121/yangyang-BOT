using Hime.Commands;
using Microsoft.Extensions.Logging;
using Sora.Entities.Events;

namespace Hime.Messaging;

/// <summary>
/// Routes an already accepted, deduplicated message to exactly one command module.
/// Platform adapters never invoke commands directly.
/// </summary>
public sealed class MessageCommandRouter(
    AdminCommand admin,
    StickerCommand stickers,
    AiCommand ai,
    VoiceCommand voice,
    MusicCommand music,
    GalleryCommand gallery,
    GroupResponseCommand groupResponse,
    UpdateLogCommand updates,
    HelpCommand help,
    JobCommand jobs,
    ILogger<MessageCommandRouter> logger)
{
    public async ValueTask<bool> TryRouteAsync(
        IncomingMessage incoming,
        string? rawText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = incoming.NativeEvent;
        var text = rawText?.Trim() ?? string.Empty;
        if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await PingCommand.Ping(message);
            return true;
        }

        if (text.Equals("hello", StringComparison.OrdinalIgnoreCase))
        {
            await ExampleCommands.Hello(message);
            return true;
        }

        if (!text.StartsWith("/", StringComparison.Ordinal))
            return false;

        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var command = separator < 0 ? text : text[..separator];
        var normalized = command.ToLowerInvariant();

        switch (normalized)
        {
            case "/admin":
            case "/\u7ba1\u7406":
                await admin.Execute(message);
                break;
            case "/sticker":
            case "/\u8868\u60c5":
                await stickers.Execute(message);
                break;
            case "/ai":
                await ai.Chat(message);
                break;
            case "/voice":
                await voice.Speak(message);
                break;
            case "/music":
            case "/song":
            case "/\u70b9\u6b4c":
                await music.Search(message);
                break;
            case "/pick":
            case "/\u9009\u6b4c":
                await music.Pick(message);
                break;
            case "/gallery":
            case "/art":
            case "/\u7f8e\u56fe":
                await gallery.Send(message);
                break;
            case "/\u54cd\u5e94":
                await groupResponse.Enable(message);
                break;
            case "/\u505c\u6b62":
                await groupResponse.Disable(message);
                break;
            case "/\u66f4\u65b0\u65e5\u5fd7":
                await updates.UpdateLog(message);
                break;
            case "/\u7248\u672c":
                await updates.Version(message);
                break;
            case "/\u63d0\u9192":
                await jobs.ReminderAsync(incoming, text, cancellationToken);
                break;
            case "/\u4efb\u52a1":
                await jobs.TasksAsync(incoming, text, cancellationToken);
                break;
            case "/help":
            case "/\u5e2e\u52a9":
            case "/?":
                if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
                    return false;
                await help.Help(message);
                break;
            default:
                return false;
        }

        logger.LogDebug("Command routed through the unified message pipeline ({Command})", command);
        return true;
    }
}
