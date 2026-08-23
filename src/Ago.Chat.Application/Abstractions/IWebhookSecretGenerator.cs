namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// Produces the plaintext webhook secret shown to the caller exactly once, at registration
/// (`RegisterWebhookEndpointHandler`). Non-deterministic by nature (a cryptographically random value),
/// the same reason `Guid.NewGuid()`/`DateTime.Now` are kept out of Application (clean-architecture.md,
/// arch-tests) despite not appearing on that literal banned-API list: a handler that called a CSPRNG
/// directly would be untestable for anything beyond "a non-empty string came back," the same gap
/// `IIdGenerator`/`IClock` exist to close for identity and time.
/// </summary>
public interface IWebhookSecretGenerator
{
    /// <summary>A high-entropy value suitable as an HMAC-SHA256 key (`adr/00XX`) - never a UUID or
    /// anything else with a fixed, guessable structure.</summary>
    string NewSecret();
}
