using Hime.Data.Models;

namespace Hime.Data.Services;

public interface IRelationshipTrajectoryService
{
    DateTime AcceptEventsAfterUtc { get; }

    bool Accepts(DateTime utc);

    string RecordUserMessage(
        long messageId,
        long userId,
        string nickname,
        long? groupId,
        string content,
        IReadOnlyList<string>? imagePaths = null,
        IReadOnlyList<long>? explicitlyAddressedUserIds = null,
        string platform = "qq");

    YangyangInteractionPlan BuildPlan(
        long messageId,
        long userId,
        string nickname,
        long? groupId,
        string? groupName,
        string currentText);

    string BuildGroupContext(long groupId, string? groupName, IReadOnlyCollection<long>? activeMemberIds = null);

    void RecordAssistantReply(
        long replyToMessageId,
        long userId,
        long? groupId,
        string content,
        string? emotion,
        string source);

    void RecordInferenceProposals(
        long evidenceMessageId,
        long userId,
        long? groupId,
        IReadOnlyCollection<PersonaMemoryProposal> proposals);
}
