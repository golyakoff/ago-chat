using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct AcceptanceRecordId(Guid Value) : IStronglyTypedId;
