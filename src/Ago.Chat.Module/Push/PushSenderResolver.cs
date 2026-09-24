using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Module.Push;

/// <summary>
/// `26-100`/`adr/0181`: the composition-root mapping behind <see cref="IPushSenderResolver"/> - a plain
/// dictionary from <see cref="PushProvider"/> to the (already resilience-wrapped) <see cref="IPushSender"/>
/// for that transport, built once in <c>Ago.Chat.Worker</c>'s own root. It lives in the Module, next to
/// <see cref="ResilientPushSender"/>, for the identical reason that wrapper does: composition in the
/// composition assembly, not in Application (which the dependency rule forbids from constructing an
/// Infrastructure adapter) and not inline in the host (a testable class is better than a closure).
///
/// <para>Each value in the map is a distinct <see cref="ResilientPushSender"/> over a distinct
/// <see cref="PushResiliencePipeline"/> instance, so an FCM outage trips only FCM's breaker and never
/// stops RuStore fallback sends - the same per-provider breaker isolation
/// <c>ChannelResiliencePipelines</c> keys per <see cref="ChannelKind"/> for exactly this reason.</para>
/// </summary>
public sealed class PushSenderResolver(IReadOnlyDictionary<PushProvider, IPushSender> senders) : IPushSenderResolver
{
    public IPushSender Resolve(PushProvider provider) =>
        senders.TryGetValue(provider, out var sender)
            ? sender
            : throw new InvalidOperationException(
                $"No {nameof(IPushSender)} is registered for push provider '{provider}'. A device row names a "
                + "transport this host has no adapter for - a misconfiguration, not an expected branch.");

    /// <summary>Maps every <see cref="PushProvider"/> to one sender - a convenience for tests and for any
    /// context that deliberately routes all transports through a single sender. Production wiring builds
    /// the dictionary explicitly instead, one resilience-wrapped adapter per provider.</summary>
    public static PushSenderResolver Single(IPushSender sender) =>
        new(Enum.GetValues<PushProvider>().ToDictionary(provider => provider, _ => sender));
}
