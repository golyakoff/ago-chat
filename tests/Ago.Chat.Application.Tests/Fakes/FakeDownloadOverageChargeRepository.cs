using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-84`: <see cref="IDownloadOverageChargeRepository"/> in memory - the whole port is one
/// insert, so the fake is one list.</summary>
public sealed class FakeDownloadOverageChargeRepository : IDownloadOverageChargeRepository
{
    public List<DownloadOverageCharge> Saved { get; } = [];

    public Task SaveAsync(DownloadOverageCharge charge, CancellationToken cancellationToken)
    {
        Saved.Add(charge);
        return Task.CompletedTask;
    }
}
