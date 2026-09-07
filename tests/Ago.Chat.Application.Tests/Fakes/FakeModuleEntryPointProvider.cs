using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`23-92`/`adr/0154`: a scripted double for <see cref="IModuleEntryPointProvider"/> - defaults
/// to answering every module key with <see cref="DefaultEntryPoint"/> so every existing owner-grant test
/// keeps working unchanged, and lets a test clear the map (or seed a different one) to prove the
/// "not declared" refusal (<c>ConversationErrors.ModuleEntryPointNotConfigured</c>) and the "resolved
/// from configuration, not from the caller" claim.</summary>
public sealed class FakeModuleEntryPointProvider : IModuleEntryPointProvider
{
    public static readonly Uri DefaultEntryPoint = new("https://module.example.com");

    private readonly Dictionary<string, Uri> _entryPoints;

    public FakeModuleEntryPointProvider(Uri? defaultEntryPoint = null)
    {
        _entryPoints = new Dictionary<string, Uri>(StringComparer.Ordinal);
        if (defaultEntryPoint is not null)
        {
            DefaultForEveryKey = defaultEntryPoint;
        }
    }

    /// <summary>When set, answers every key not explicitly seeded with this value - the common case, so
    /// a test that does not care about entry point resolution need not seed one. <see langword="null"/>
    /// (the default once <see cref="Clear"/> has been called) means an unseeded key resolves to nothing,
    /// the "this deployment declared nothing" case.</summary>
    public Uri? DefaultForEveryKey { get; set; } = DefaultEntryPoint;

    public void Seed(ModuleKey moduleKey, Uri entryPoint) => _entryPoints[moduleKey.Value] = entryPoint;

    /// <summary>Makes every key resolve to nothing, including ones not yet seeded - the "deployment has
    /// declared nothing at all" state <c>ConfiguredModuleEntryPointProvider</c> returns for a blank
    /// section.</summary>
    public void Clear()
    {
        _entryPoints.Clear();
        DefaultForEveryKey = null;
    }

    public Uri? TryGet(ModuleKey moduleKey) =>
        _entryPoints.TryGetValue(moduleKey.Value, out var entryPoint) ? entryPoint : DefaultForEveryKey;
}
