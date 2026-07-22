using Hime.Data.Models;

namespace Hime.Data.Services;

public interface IPersonaStateService
{
    void ObserveConversation(long userId, string nickname, long? groupId, string? groupName = null);

    void RecordAssistantReply(long userId, long? groupId, string emotion);

    void ApplyMemoryProposals(long userId, long? groupId, IReadOnlyCollection<PersonaMemoryProposal> proposals);

    string BuildPromptContext(long userId, string nickname, long? groupId, string? groupName = null);

    string BuildGroupPromptContext(
        long groupId,
        string? groupName = null,
        IReadOnlyCollection<long>? activeMemberIds = null);
}
