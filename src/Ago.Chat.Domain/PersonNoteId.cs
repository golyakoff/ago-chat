using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct PersonNoteId(Guid Value) : IStronglyTypedId;
