using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

/// <summary>`20-07`: identifies one row of "site X has module K enabled" - see
/// <see cref="EnabledModule"/>.</summary>
public readonly record struct EnabledModuleId(Guid Value) : IStronglyTypedId;
