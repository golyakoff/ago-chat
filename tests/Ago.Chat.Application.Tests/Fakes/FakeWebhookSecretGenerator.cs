using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Settable in a test, never ambient - the same reason <see cref="FakeClock"/> exposes a
/// mutable property instead of a real clock.</summary>
public sealed class FakeWebhookSecretGenerator(string secret = "whsec_test-secret") : IWebhookSecretGenerator
{
    public string Secret { get; set; } = secret;

    public string NewSecret() => Secret;
}
