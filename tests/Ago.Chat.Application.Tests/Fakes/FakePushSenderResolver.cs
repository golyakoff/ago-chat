using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`26-100`: `NotifyOperatorDevicesHandlerTests`' own fake of the resolver port. One-sender
/// constructor maps every provider to the same fake (the shape almost every existing test wants, since it
/// only cares that the handler sent, not which transport); the dictionary constructor maps a distinct fake
/// per provider, for the one test that proves the handler routes by `OperatorDevice.Provider`.</summary>
public sealed class FakePushSenderResolver : IPushSenderResolver
{
    private readonly IReadOnlyDictionary<PushProvider, IPushSender> _senders;

    public FakePushSenderResolver(IPushSender sender) =>
        _senders = Enum.GetValues<PushProvider>().ToDictionary(provider => provider, _ => sender);

    public FakePushSenderResolver(IReadOnlyDictionary<PushProvider, IPushSender> senders) => _senders = senders;

    public IPushSender Resolve(PushProvider provider) => _senders[provider];
}
