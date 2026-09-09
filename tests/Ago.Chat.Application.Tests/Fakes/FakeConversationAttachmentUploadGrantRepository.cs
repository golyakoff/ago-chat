using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Mirrors the real <c>ConversationAttachmentUploadGrantRepository</c>'s own three-way,
/// atomic contract - the identical shape <see cref="FakeConversationBlockRepository"/> already
/// establishes for its own sibling interface, minus that fake's own <c>Records</c> audit trail: this
/// item builds no append-only history table for itself
/// (<see cref="IConversationAttachmentUploadGrantRepository"/>'s own remarks), so there is nothing here
/// to record beyond the current-state pair.</summary>
public sealed class FakeConversationAttachmentUploadGrantRepository : IConversationAttachmentUploadGrantRepository
{
    private readonly Dictionary<(ConversationId, SiteId), OperatorId?> _grantedBy = [];

    public int GrantCalls { get; private set; }

    public int RevokeCalls { get; private set; }

    public void SeedConversation(ConversationId conversationId, SiteId siteId, bool granted = false) =>
        _grantedBy[(conversationId, siteId)] = granted ? new OperatorId(Guid.NewGuid()) : null;

    public Task<AttachmentUploadGrantOutcome> GrantAsync(
        ConversationId conversationId, SiteId siteId, OperatorId grantedBy, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        GrantCalls++;
        var key = (conversationId, siteId);
        if (!_grantedBy.TryGetValue(key, out var current))
        {
            return Task.FromResult(AttachmentUploadGrantOutcome.NotFound);
        }

        if (current is not null)
        {
            return Task.FromResult(AttachmentUploadGrantOutcome.AlreadyInState);
        }

        _grantedBy[key] = grantedBy;
        return Task.FromResult(AttachmentUploadGrantOutcome.Applied);
    }

    public Task<AttachmentUploadGrantOutcome> RevokeAsync(
        ConversationId conversationId, SiteId siteId, OperatorId revokedBy, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RevokeCalls++;
        var key = (conversationId, siteId);
        if (!_grantedBy.TryGetValue(key, out var current))
        {
            return Task.FromResult(AttachmentUploadGrantOutcome.NotFound);
        }

        if (current is null)
        {
            return Task.FromResult(AttachmentUploadGrantOutcome.AlreadyInState);
        }

        _grantedBy[key] = null;
        return Task.FromResult(AttachmentUploadGrantOutcome.Applied);
    }
}
