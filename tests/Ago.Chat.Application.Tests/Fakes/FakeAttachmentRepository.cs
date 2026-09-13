using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeAttachmentRepository : IAttachmentRepository
{
    private readonly Dictionary<AttachmentId, Attachment> _byId = [];

    public Task<Attachment?> GetByIdAsync(AttachmentId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task SaveAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        _byId[attachment.Id] = attachment;
        return Task.CompletedTask;
    }

    public void Seed(Attachment attachment) => _byId[attachment.Id] = attachment;

    /// <summary>`23-76`: mirrors `AttachmentRepository.FindReadyDuplicateAsync`'s own predicate
    /// exactly - same site, same hash, `Ready`, excluding the caller's own id, oldest first.</summary>
    public Task<Attachment?> FindReadyDuplicateAsync(
        SiteId siteId, string contentHash, AttachmentId excluding, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values
            .Where(a => a.SiteId == siteId && a.ContentHash == contentHash && a.State == AttachmentState.Ready && a.Id != excluding)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefault());
}
