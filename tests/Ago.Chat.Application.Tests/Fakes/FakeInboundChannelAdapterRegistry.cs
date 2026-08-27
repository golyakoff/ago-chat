using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Test double for <see cref="IInboundChannelAdapterRegistry"/> - a plain dictionary lookup,
/// deliberately simpler than the real <c>InboundChannelAdapterRegistry</c> (no duplicate-registration
/// guard): a test wants to control exactly which adapter, if any, answers for a given
/// <see cref="ChannelKind"/>, not to re-prove that class's own construction-time check.</summary>
public sealed class FakeInboundChannelAdapterRegistry : IInboundChannelAdapterRegistry
{
    private readonly Dictionary<ChannelKind, IInboundChannelAdapter> _byKind = [];

    public void Register(IInboundChannelAdapter adapter) => _byKind[adapter.Kind] = adapter;

    public IInboundChannelAdapter? For(ChannelKind kind) => _byKind.GetValueOrDefault(kind);
}
