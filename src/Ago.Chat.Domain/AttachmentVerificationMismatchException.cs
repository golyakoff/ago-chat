namespace Ago.Chat.Domain;

/// <summary>
/// `5-03`'s Done-when: "confirming without a real upload / mismatched size or type fails, stays
/// pending" - the object <see cref="IFileStorage"/> actually HEAD-verified does not match what was
/// declared at presign time. Not an <see cref="InvalidAttachmentStateException"/>: the attachment
/// stays exactly in <see cref="AttachmentState.Pending"/> after this, ready to be confirmed again
/// once the real upload lands, rather than moving to a terminal failure state that would need one.
/// </summary>
public sealed class AttachmentVerificationMismatchException(string message) : Exception(message);
