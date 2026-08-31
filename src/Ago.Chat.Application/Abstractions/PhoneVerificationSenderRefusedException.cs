namespace Ago.Chat.Application.Abstractions;

/// <summary>`14-15`: the identical shape and reasoning `Ago.Chat.Infrastructure.YandexGpt.ReplyDraftProviderRefusedException`
/// already establishes for its own provider - a terminal, our-own-fault-or-the-number's-own-fault refusal
/// (a malformed or gateway-rejected phone number, bad credentials) a retry could never fix. Lives beside
/// <see cref="IPhoneVerificationSender"/> rather than in a concrete `Infrastructure.*` project, unlike
/// that sibling, because no concrete gateway project exists yet to own it - the port and its one
/// documented terminal exception are what `IPhoneVerificationSender`'s own remarks call "the port shape
/// [being] real" even before an account does.</summary>
public sealed class PhoneVerificationSenderRefusedException(string message) : Exception(message);
