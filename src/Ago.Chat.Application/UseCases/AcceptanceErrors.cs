using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>Error codes for `24-01`'s own use cases - one small vocabulary, the same
/// one-file-per-feature-area shape <see cref="ConversationErrors"/> already establishes.</summary>
public static class AcceptanceErrors
{
    public static Error Invalid(string reason) => new("Acceptance.Invalid", reason);
}
