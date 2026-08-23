using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct WebhookDeliveryId(Guid Value) : IStronglyTypedId;
