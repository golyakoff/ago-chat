using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct PendingPhoneVerificationId(Guid Value) : IStronglyTypedId;
