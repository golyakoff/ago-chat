using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct BillingWebhookEventId(Guid Value) : IStronglyTypedId;
