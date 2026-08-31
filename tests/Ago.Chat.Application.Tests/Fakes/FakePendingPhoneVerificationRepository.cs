using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakePendingPhoneVerificationRepository : IPendingPhoneVerificationRepository
{
    private readonly Dictionary<PendingPhoneVerificationId, PendingPhoneVerification> _byId = [];

    public IReadOnlyCollection<PendingPhoneVerification> All => _byId.Values;

    public Task<PendingPhoneVerification?> GetByIdAsync(PendingPhoneVerificationId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task SaveAsync(PendingPhoneVerification verification, CancellationToken cancellationToken)
    {
        _byId[verification.Id] = verification;
        return Task.CompletedTask;
    }

    public void Seed(PendingPhoneVerification verification) => _byId[verification.Id] = verification;
}
