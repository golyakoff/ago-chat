using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct ModuleTaskChannelPreferenceId(Guid Value) : IStronglyTypedId;
