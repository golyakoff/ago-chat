using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-100`/`adr/0181`: picks the <see cref="IPushSender"/> that speaks a given device's transport. Two
/// providers now exist - FCM (primary) and RuStore (fallback) - and a device row carries which one its
/// token belongs to (<see cref="Ago.Chat.Domain.OperatorDevice.Provider"/>), so a fan-out to an
/// operator's devices can no longer assume one sender: <c>NotifyOperatorDevicesHandler</c> resolves the
/// sender per device inside its existing loop.
///
/// <para><b>Why this is the moment the dispatch table stops being premature.</b> <see cref="IPushSender"/>'s
/// own remarks record `adr/0179` §5's rule that "a dispatch table with one entry is a guess about the
/// second" - so no resolver, no <c>IPushSenderFactory</c>, existed while RuStore was the only transport.
/// `adr/0181` introduces the real second transport, which is exactly the condition that rule set for
/// building the table: this interface has two entries because there are two providers, not in
/// anticipation of a third.
///
/// <para><b>Why the resolver lives in Application, not the host (CLAUDE.md rule 2).</b> The handler is an
/// Application use case; it must select a transport without knowing which concrete adapters exist, how
/// they are keyed in the container, or that a container exists at all. The abstraction lets a unit test
/// fake the mapping (`NotifyOperatorDevicesHandlerTests`' own fake) exactly as it already fakes a single
/// sender. The alternative - injecting <c>IServiceProvider</c> or <c>[FromKeyedServices]</c> into the
/// handler - would tie the use case to the DI container and to Infrastructure's keying, the coupling the
/// dependency rule exists to forbid. The mapping itself (enum to adapter, each with its own resilience
/// wrapper) is a composition concern and lives in <c>Ago.Chat.Module.Push.PushSenderResolver</c>, wired
/// in <c>Ago.Chat.Worker</c>'s own root the same way <see cref="IPushSender"/> and its
/// <c>ResilientPushSender</c> wrapper already are.</para>
/// </summary>
public interface IPushSenderResolver
{
    /// <summary>Returns the sender for <paramref name="provider"/>. Throws when no sender is registered
    /// for it - a misconfiguration (a device persisted with a provider the running host has no adapter
    /// for), never an expected runtime branch, so it fails loudly rather than silently dropping the push.</summary>
    IPushSender Resolve(PushProvider provider);
}
