using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct BillingSubscriptionId(Guid Value) : IStronglyTypedId;
