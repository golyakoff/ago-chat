using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Settable in a test, never ambient - the same reason <see cref="FakeWebhookSecretGenerator"/>
/// exposes a mutable property instead of a real CSPRNG.</summary>
public sealed class FakeOperatorInviteCodeGenerator(string code = "invite_test-code") : IOperatorInviteCodeGenerator
{
    public string Code { get; set; } = code;

    public string NewCode() => Code;
}
