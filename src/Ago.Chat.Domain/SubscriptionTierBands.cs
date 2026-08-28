namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: the seat-count -> tier lookup, kept as pure Domain logic (no I/O, no options dependency)
/// so `CreateCheckoutSessionHandler` can validate a requested seat count before ever calling out to
/// ЮKassa, and so a unit test can assert the boundary without a database or a fake HTTP host.
///
/// <para><b>The band-boundary reading, stated here because the backlog's own source data is ambiguous.</b>
/// `roadmap.md`'s Stage 13 planning names the bands as "Starter (2-10 seats), Growth (10-100 seats)" -
/// literally overlapping at 10. This item reads that as the natural non-overlapping split a loosely
/// worded band list almost always means: <b>Starter = 2-9 seats, Growth = 10-100 seats</b>, i.e. a
/// requested seat count belongs to Growth from the point it first reaches the number named as Growth's
/// own lower bound. This is a stated, correctable reading of ambiguous given data, not a genuine
/// product-policy call of the kind this stage blocks liberally on elsewhere - the backlog item's own
/// Open Questions section says so explicitly. If ago-business's real pricing meant something else,
/// this is the one method to correct.</para>
///
/// <para>1 seat has no band at all - that is the free tier every site starts on
/// (<see cref="Site.Tier"/>'s own default), never something a checkout purchases. 0 or negative is
/// simply invalid input. Above 100 has no band either: nothing in the given pricing data describes an
/// Enterprise tier or an uncapped seat count, so this item does not invent one - a seat count outside
/// [2, 100] is rejected by <see cref="TryResolveTier"/> the same way an out-of-range value would be by
/// a value object's own constructor, just expressed as a lookup miss instead of a thrown exception,
/// since the caller (<c>CreateCheckoutSessionHandler</c>) needs a <c>Result</c> failure, not an
/// unhandled exception, for an ordinary bad-input case a real client will trigger by mistake.</para>
/// </summary>
public static class SubscriptionTierBands
{
    public const string Starter = "starter";

    public const string Growth = "growth";

    public const int MinSeats = 2;

    public const int MaxSeats = 100;

    private const int GrowthMinSeats = 10;

    /// <summary>
    /// <see langword="true"/> and the resolved tier name for any seat count in [<see cref="MinSeats"/>,
    /// <see cref="MaxSeats"/>]; <see langword="false"/> (and an empty <paramref name="tier"/>) for
    /// anything outside that range, including the free tier's own single seat.
    /// </summary>
    public static bool TryResolveTier(int requestedSeats, out string tier)
    {
        if (requestedSeats < MinSeats || requestedSeats > MaxSeats)
        {
            tier = string.Empty;
            return false;
        }

        tier = requestedSeats >= GrowthMinSeats ? Growth : Starter;
        return true;
    }
}
