using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Module.Channels;

/// <summary>
/// `14-01`: implements <see cref="IInboundChannelAdapterRegistry"/> over whatever
/// <see cref="IInboundChannelAdapter"/> implementations the host happened to register.
///
/// <para>Lives in <c>Ago.Chat.Module</c> rather than a new <c>Ago.Chat.Infrastructure.Channels</c>
/// project, for the reason <c>ChannelMessagePipeline</c>'s own remarks already set out: a dedicated
/// Infrastructure project advertises "a second implementation could reasonably replace this one", the
/// way <c>Ago.Chat.Infrastructure.Postgres</c> could be swapped for another database. There is nothing
/// swappable about a dictionary lookup. The adapters it resolves are a different matter - each of
/// those is genuinely a provider adapter, and `14-02`/`14-03` are free to give theirs its own project
/// or host.</para>
///
/// <para><b>Duplicate kinds fail at construction, not at first use.</b> Two adapters claiming
/// <see cref="ChannelKind.Sms"/> is a misconfiguration whose only honest outcome is a startup failure -
/// picking one silently would route half a tenant's replies through a provider nobody chose, and the
/// symptom would surface days later as "some messages never arrived". This is the same reasoning
/// <c>ChatModule</c> applies to <c>WebhookSecretCipherOptions</c>' <c>ValidateOnStart()</c>.</para>
/// </summary>
public sealed class InboundChannelAdapterRegistry : IInboundChannelAdapterRegistry
{
    private readonly Dictionary<ChannelKind, IInboundChannelAdapter> _byKind;

    public InboundChannelAdapterRegistry(IEnumerable<IInboundChannelAdapter> adapters)
    {
        _byKind = [];
        foreach (var adapter in adapters)
        {
            if (!_byKind.TryAdd(adapter.Kind, adapter))
            {
                throw new InvalidOperationException(
                    $"More than one {nameof(IInboundChannelAdapter)} is registered for {adapter.Kind}: " +
                    $"{_byKind[adapter.Kind].GetType().Name} and {adapter.GetType().Name}. " +
                    "Exactly one adapter may serve a channel.");
            }
        }
    }

    public IInboundChannelAdapter? For(ChannelKind kind) => _byKind.GetValueOrDefault(kind);
}
