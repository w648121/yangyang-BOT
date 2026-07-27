using Hime.Commands;
using Hime.Data.Services;
using Hime.Messaging.Interactions;
using Hime.Services;
using Sora.Entities.Events;

namespace Hime.Messaging;

public sealed record GenerateAiReplyCommand(
    IncomingMessage Message,
    string Prompt) : ICommand;

public sealed class GenerateAiReplyCommandHandler(
    AiCommand ai) : ICommandHandler<GenerateAiReplyCommand>
{
    public Task HandleAsync(
        GenerateAiReplyCommand command,
        CancellationToken cancellationToken) =>
        ai.DoChat(command.Message, command.Prompt);
}

public sealed record ClearAiConversationCommand(MessageReceivedEvent Message) : ICommand;

public sealed class ClearAiConversationCommandHandler(AiCommand ai) :
    ICommandHandler<ClearAiConversationCommand>
{
    public Task HandleAsync(
        ClearAiConversationCommand command,
        CancellationToken cancellationToken) =>
        ai.ClearConversationAsync(command.Message);
}

public sealed record HandleGsCoreCommand(
    MessageReceivedEvent Message,
    string RawText) : ICommand;

public sealed class HandleGsCoreCommandHandler(GsCoreBridgeService bridge) :
    ICommandHandler<HandleGsCoreCommand>
{
    public Task HandleAsync(
        HandleGsCoreCommand command,
        CancellationToken cancellationToken) =>
        bridge.HandleAsync(command.Message, command.RawText, cancellationToken);
}

public sealed record PlayMusicRequestCommand(
    MessageReceivedEvent Message,
    string Keywords) : ICommand;

public sealed class PlayMusicRequestCommandHandler(
    MusicCommand music) : ICommandHandler<PlayMusicRequestCommand>
{
    public async Task HandleAsync(
        PlayMusicRequestCommand command,
        CancellationToken cancellationToken)
    {
        await music.PlayFirstAsync(command.Message, command.Keywords);
    }
}

public sealed record SendSetuRequestCommand(
    MessageReceivedEvent Message,
    SetuRequest Request) : ICommand;

public sealed class SendSetuRequestCommandHandler(
    SetuRequestService setu) : ICommandHandler<SendSetuRequestCommand>
{
    public Task HandleAsync(
        SendSetuRequestCommand command,
        CancellationToken cancellationToken) =>
        setu.HandleAsync(command.Message, command.Request, cancellationToken);
}

public sealed record TryReactiveConversationCommand(
    IncomingMessage Message,
    string RawText,
    ConversationFocusDecision? Focus,
    bool StickerReplyAlreadySent) : ICommand;

public sealed class TryReactiveConversationCommandHandler(
    ReactiveConversationService conversations) : ICommandHandler<TryReactiveConversationCommand>
{
    public async Task HandleAsync(
        TryReactiveConversationCommand command,
        CancellationToken cancellationToken)
    {
        await conversations.TryReplyAsync(
            command.Message,
            command.RawText,
            command.Focus,
            command.StickerReplyAlreadySent,
            cancellationToken);
    }
}

public static class InteractionKinds
{
    public const string PrivateAiPrompt = "private-ai-prompt";
    public const string JobDraft = "job-draft";
}

public sealed class PrivateAiPromptContinuationHandler(
    ICommandBus commandBus,
    IInteractionManager interactions) : IInteractionContinuationHandler
{
    public string Kind => InteractionKinds.PrivateAiPrompt;

    public async Task ResumeAsync(
        MessageContext context,
        PendingInteraction interaction,
        CancellationToken cancellationToken)
    {
        var prompt = context.Message.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) && !context.Message.ContainsVisual)
        {
            await AiCommand.Reply(context.Message.NativeEvent, "还没有收到内容，请继续发送问题；发送“取消”可退出等待。");
            await interactions.RegisterAsync(
                interaction with
                {
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
                },
                cancellationToken);
            return;
        }

        await commandBus.SendAsync(
            new GenerateAiReplyCommand(context.Message, prompt),
            cancellationToken);
    }
}
