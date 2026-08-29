using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

/// <summary>`20-07`: AGO Chat's own id for one <see cref="ModuleTask"/> - distinct from
/// <see cref="ModuleTask.ExternalTaskId"/>, which the module mints and Chat never generates or
/// interprets (`adr/0065` decision 1).</summary>
public readonly record struct ModuleTaskId(Guid Value) : IStronglyTypedId;
