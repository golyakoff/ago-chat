namespace Ago.Chat.Domain;

/// <summary>
/// An author id did not match the conversation's visitor or its assigned operator. This is a fact
/// about the conversation, not an authorization decision (adr/0016 draws that line) - the RBAC
/// permission check already happened in Application before this method was ever called.
/// </summary>
public sealed class ConversationParticipantMismatchException(string message) : Exception(message);
