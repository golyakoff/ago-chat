using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`23-102`: a scripted double for <see cref="IModulePermissionsProvider"/> - the same "default
/// answers every key, a test overrides or clears when it cares" shape
/// <see cref="FakeModuleEntryPointProvider"/> already establishes for its sibling port.</summary>
public sealed class FakeModulePermissionsProvider : IModulePermissionsProvider
{
    private readonly Dictionary<string, ModulePermissionSet> _permissions = new(StringComparer.Ordinal);

    /// <summary>Unseeded keys answer with this - <see cref="ModulePermissionSet.Empty"/> by default (the
    /// "this deployment declared nothing for this module" case), the same legitimate-not-a-refusal
    /// answer <c>ConfiguredModulePermissionsProvider</c> gives for the identical case.</summary>
    public ModulePermissionSet DefaultForEveryKey { get; set; } = ModulePermissionSet.Empty;

    public void Seed(ModuleKey moduleKey, ModulePermissionSet permissions) => _permissions[moduleKey.Value] = permissions;

    public ModulePermissionSet Get(ModuleKey moduleKey) =>
        _permissions.TryGetValue(moduleKey.Value, out var permissions) ? permissions : DefaultForEveryKey;
}
