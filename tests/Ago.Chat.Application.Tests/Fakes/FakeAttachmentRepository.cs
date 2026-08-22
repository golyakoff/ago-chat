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
}
