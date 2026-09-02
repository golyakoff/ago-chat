namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// Thrown by <see cref="IChannelPollerLease.VerifyStillHeldAsync"/> when the connection backing a
/// lease is no longer usable - `adr/0089`'s half-open-connection case, surfaced rather than silently
/// tolerated. Not a bug and not logged as one: the caller's correct reaction is to stop polling this
/// credential and let the next refresh tick retry, exactly as if
/// <see cref="IChannelPollerOwnership.TryAcquireAsync"/> had returned <see langword="null"/> to begin
/// with.
/// </summary>
public sealed class ChannelPollerLeaseLostException(string message, Exception innerException)
    : Exception(message, innerException);
