using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Returns a canned row (or <see langword="null"/>) regardless of what hash it is asked
/// about - the same "fake returns a fixed answer, the handler test proves the mapping around it" shape
/// <see cref="FakeOperatorInviteRedemptionRepository"/> already uses for its own sibling handler.</summary>
public sealed class FakeOperatorInvitePreviewReadStore(OperatorInvitePreviewItem? item) : IOperatorInvitePreviewReadStore
{
    public byte[]? LastCodeHash { get; private set; }

    public Task<OperatorInvitePreviewItem?> GetByCodeHashAsync(byte[] codeHash, CancellationToken cancellationToken)
    {
        LastCodeHash = codeHash;
        return Task.FromResult(item);
    }
}
