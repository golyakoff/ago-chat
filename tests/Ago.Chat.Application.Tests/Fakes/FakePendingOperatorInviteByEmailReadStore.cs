using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakePendingOperatorInviteByEmailReadStore : IPendingOperatorInviteByEmailReadStore
{
    private readonly HashSet<string> _pendingEmails = [];

    public string? LastEmailChecked { get; private set; }

    public void SeedPending(string email) => _pendingEmails.Add(email);

    public Task<bool> AnyPendingForEmailAsync(string email, DateTimeOffset now, CancellationToken cancellationToken)
    {
        LastEmailChecked = email;
        return Task.FromResult(_pendingEmails.Contains(email));
    }
}
