using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeSubscriptionRenewalApplier : ISubscriptionRenewalApplier
{
    public List<BillingSubscriptionId> Lapsed { get; } = [];

    public List<BillingSubscriptionId> RenewedSuccessfully { get; } = [];

    public List<BillingSubscriptionId> RenewalFailures { get; } = [];

    public Task ApplyLapseAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Lapsed.Add(id);
        return Task.CompletedTask;
    }

    public Task ApplyRenewalSuccessAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RenewedSuccessfully.Add(id);
        return Task.CompletedTask;
    }

    public Task ApplyRenewalFailureAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RenewalFailures.Add(id);
        return Task.CompletedTask;
    }
}
