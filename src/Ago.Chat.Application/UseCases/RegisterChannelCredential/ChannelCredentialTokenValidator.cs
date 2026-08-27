namespace Ago.Chat.Application.UseCases.RegisterChannelCredential;

/// <summary>
/// `14-02`: the one check every provider's own bot token shares - non-empty, bounded - the same
/// "validate only what is true of every channel" discipline
/// <see cref="Domain.ExternalChannelAddress"/> already applies for the inbound address. A provider's own
/// token *format* (MAX's own shape is undocumented beyond "opaque string sent in an `Authorization`
/// header") is not this validator's business - `WebhookUrlValidator`'s own split between "Application
/// validates the generic shape, the concrete caller/adapter owns anything provider-specific" is the
/// precedent.
/// </summary>
public static class ChannelCredentialTokenValidator
{
    // Generous enough for any bot-platform token seen in the wild (MAX's own tokens are far shorter);
    // bounded so a malformed console request cannot push an unbounded string through encryption and
    // into storage. No product requirement pins the exact number, the same "no product requirement
    // pins it" ExternalChannelAddress.MaxLength states for the identical kind of bound.
    public const int MaxLength = 512;

    public static string? Validate(string token) =>
        string.IsNullOrWhiteSpace(token) ? "The channel token cannot be empty."
        : token.Trim().Length > MaxLength ? $"The channel token cannot exceed {MaxLength} characters."
        : null;
}
