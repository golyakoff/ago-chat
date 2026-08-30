using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Settable in a test, never ambient - the same reason <see cref="FakeOperatorInviteCodeGenerator"/>
/// exposes a mutable property instead of a real CSPRNG.</summary>
public sealed class FakePendingChannelLinkCodeGenerator(string code = "482913") : IPendingChannelLinkCodeGenerator
{
    public string Code { get; set; } = code;

    public string NewCode() => Code;
}
