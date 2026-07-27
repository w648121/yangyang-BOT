using System.Security.Cryptography;
using System.Text;
using Hime.Data.Services;
using Hime.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sora.Core.Enums;
using Sora.Entities.Events;
using Sora.Entities.Message;
using Sora.Entities.Segments;
using SoraMessageContext = Sora.Entities.Message.MessageContext;

namespace Hime.Services;

/// <summary>
/// Expands incoming QQ merged-forward cards into bounded group-chat evidence.
/// Forwarded nodes are archived for context/investigation only and never re-enter
/// the command or AI reply pipeline.
/// </summary>
public sealed class ForwardMessageIngestOptions
{
    public bool Enabled { get; set; } = true;

    public int MaxForwardIdsPerMessage { get; set; } = 3;

    public int MaxNodesPerForward { get; set; } = 80;

    public int MaxTextCharactersPerNode { get; set; } = 360;

    public int MaxMentionedUsersPerNode { get; set; } = 12;

    public bool RecordImageOnlyNodes { get; set; } = true;

    public string ImagePlaceholder { get; set; } = "[图片]";

    public string AudioPlaceholder { get; set; } = "[语音]";

    public string NestedForwardPlaceholder { get; set; } = "[合并转发]";

    public string UnknownContentPlaceholder { get; set; } = string.Empty;

    public string ContentSeparator { get; set; } = " ";

    public string ImportedMessageIdPrefix { get; set; } = "forward";

    public bool IsValid() =>
        MaxForwardIdsPerMessage is > 0 and <= 20 &&
        MaxNodesPerForward is > 0 and <= 500 &&
        MaxTextCharactersPerNode is >= 40 and <= 2000 &&
        MaxMentionedUsersPerNode is >= 0 and <= 100 &&
        ContentSeparator is not null &&
        ImportedMessageIdPrefix is { Length: > 0 and <= 40 };
}

public sealed record ForwardMessageIngestResult(
    int ForwardCards,
    int FetchedNodes,
    int RecordedNodes,
    int FailedCards)
{
    public static ForwardMessageIngestResult Empty { get; } = new(0, 0, 0, 0);
}

public sealed class ForwardMessageIngestService(
    IGroupActivityService groupActivities,
    IOptionsMonitor<ForwardMessageIngestOptions> options,
    ILogger<ForwardMessageIngestService> logger)
{
    public async Task<ForwardMessageIngestResult> IngestAsync(
        MessageReceivedEvent e,
        IncomingMessage incoming,
        string groupName,
        CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!current.Enabled ||
            e.Message.SourceType != MessageSourceType.Group ||
            !incoming.IsGroup ||
            incoming.ForwardIds.Count == 0)
        {
            return ForwardMessageIngestResult.Empty;
        }

        var forwardCards = 0;
        var fetchedNodes = 0;
        var recordedNodes = 0;
        var failedCards = 0;

        foreach (var forwardId in incoming.ForwardIds
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.Ordinal)
                     .Take(current.MaxForwardIdsPerMessage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            forwardCards++;

            IReadOnlyList<SoraMessageContext> nodes;
            try
            {
                var apiResult = await e.Api.GetForwardMessagesAsync(forwardId, cancellationToken);
                if (!apiResult.IsSuccess)
                {
                    failedCards++;
                    logger.LogWarning(
                        "Unable to fetch merged-forward content (GroupId={GroupId}, ForwardId={ForwardId}, Code={Code}, Message={Message})",
                        incoming.GroupId,
                        forwardId,
                        apiResult.Code,
                        apiResult.Message);
                    continue;
                }

                nodes = apiResult.Data ?? [];
            }
            catch (Exception ex)
            {
                failedCards++;
                logger.LogWarning(
                    ex,
                    "Unable to fetch merged-forward content (GroupId={GroupId}, ForwardId={ForwardId})",
                    incoming.GroupId,
                    forwardId);
                continue;
            }

            foreach (var node in nodes.Take(current.MaxNodesPerForward))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fetchedNodes++;

                var normalized = NormalizeNode(node, current);
                if (string.IsNullOrWhiteSpace(normalized.Content))
                    continue;

                var senderId = node.SenderId > 0 ? (long)node.SenderId : incoming.SenderId;
                var senderName = string.IsNullOrWhiteSpace(node.SenderName)
                    ? senderId.ToString()
                    : node.SenderName;
                var messageId = CreateSyntheticMessageId(
                    current.ImportedMessageIdPrefix,
                    incoming.AccountId,
                    incoming.GroupId!.Value,
                    incoming.MessageId,
                    forwardId,
                    fetchedNodes);

                groupActivities.RecordIncoming(
                    incoming.GroupId.Value,
                    groupName,
                    senderId,
                    senderName,
                    normalized.Content,
                    [],
                    messageId: messageId,
                    accountId: incoming.AccountId,
                    mentionedUserIds: normalized.MentionedUserIds,
                    topicId: incoming.TopicId,
                    conversationParticipants: incoming.ConversationParticipants);
                recordedNodes++;
            }
        }

        if (recordedNodes > 0 || failedCards > 0)
        {
            logger.LogInformation(
                "Merged-forward ingest completed (GroupId={GroupId}, Cards={Cards}, Fetched={Fetched}, Recorded={Recorded}, Failed={Failed})",
                incoming.GroupId,
                forwardCards,
                fetchedNodes,
                recordedNodes,
                failedCards);
        }

        return new ForwardMessageIngestResult(forwardCards, fetchedNodes, recordedNodes, failedCards);
    }

    private static ForwardNodeContent NormalizeNode(
        SoraMessageContext node,
        ForwardMessageIngestOptions options)
    {
        var segments = node.Body;
        var parts = new List<string>();
        var text = TrimTo(segments?.GetText(), options.MaxTextCharactersPerNode);
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add(text);

        if (segments?.OfType<ImageSegment>().Any() == true && options.RecordImageOnlyNodes)
            AddConfiguredPart(parts, options.ImagePlaceholder);
        if (segments?.OfType<AudioSegment>().Any() == true)
            AddConfiguredPart(parts, options.AudioPlaceholder);
        if (segments?.OfType<ForwardSegment>().Any() == true)
            AddConfiguredPart(parts, options.NestedForwardPlaceholder);

        if (parts.Count == 0)
            AddConfiguredPart(parts, options.UnknownContentPlaceholder);

        var content = string.Join(options.ContentSeparator, parts)
            .Trim();
        var mentionedUsers = segments?
            .OfType<MentionSegment>()
            .Select(mention => (long)mention.Target)
            .Where(userId => userId > 0)
            .Distinct()
            .Take(options.MaxMentionedUsersPerNode)
            .ToArray() ?? [];
        return new ForwardNodeContent(content, mentionedUsers);
    }

    private static void AddConfiguredPart(List<string> parts, string? value)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
            parts.Add(normalized);
    }

    private static string TrimTo(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static long CreateSyntheticMessageId(
        string prefix,
        string accountId,
        long groupId,
        long sourceMessageId,
        string forwardId,
        int nodeIndex)
    {
        var seed = $"{prefix}:{accountId}:{groupId}:{sourceMessageId}:{forwardId}:{nodeIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return BitConverter.ToInt64(hash, 0) & long.MaxValue;
    }

    private sealed record ForwardNodeContent(
        string Content,
        IReadOnlyList<long> MentionedUserIds);
}
