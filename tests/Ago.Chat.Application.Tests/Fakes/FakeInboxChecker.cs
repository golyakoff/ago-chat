using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// Mirrors <c>EfInboxChecker</c>'s dedup semantics (first delivery -&gt; true, a repeat of the same
/// (messageId, consumer) pair -&gt; false) via a plain in-memory set. What it deliberately cannot
/// mirror: the real checker's failed save rolling back whatever the caller staged alongside it on
/// the same `DbContext` (adr/0017) - there is no transaction here to roll back. A handler test using
/// this fake can assert the handler asks the checker the right question and returns its answer; only
/// a real-Postgres integration/concurrency test can prove a duplicate leaves the row genuinely
/// untouched.
/// </summary>
public sealed class FakeInboxChecker : IInboxChecker
{
    private readonly HashSet<(Guid MessageId, string Consumer)> _recorded = [];

    public Task<bool> TryRecordAndSaveAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
        Task.FromResult(_recorded.Add((messageId, consumer)));
}
