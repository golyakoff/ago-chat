using Ago.Chat.Domain;

namespace Ago.Chat.Domain.Tests;

/// <summary>`25-84`: the metered charge itself, as a pure function - `docs/backlog/25-84-*.md`'s own
/// Done-when asks for "a charge computed from real gigabytes-over-threshold... provably correct against
/// a fabricated egress figure, not merely 'some amount was charged'", and these are the fabricated
/// figures.</summary>
public class DownloadOveragePricingTests
{
    private const decimal ShippedPricePerGigabyteRub = 100m;

    /// <summary>The shipped default, against exactly one gigabyte over: 100 RUB, not a rounded
    /// approximation of it.</summary>
    [Fact]
    public void ComputeOverageRub_ChargesTheShippedPrice_ForExactlyOneGigabyteOver()
    {
        var amount = DownloadOveragePricing.ComputeOverageRub(
            DownloadOveragePricing.BytesPerGigabyte, ShippedPricePerGigabyteRub);

        Assert.Equal(100.00m, amount);
    }

    /// <summary>A partial gigabyte costs its real fraction - not a whole "started gigabyte", which is
    /// the surprise this type's own remarks reject in full. Half a gibibyte at 100 RUB/GiB is 50 RUB.</summary>
    [Fact]
    public void ComputeOverageRub_ChargesTheRealFraction_OfAPartialGigabyte()
    {
        var amount = DownloadOveragePricing.ComputeOverageRub(
            DownloadOveragePricing.BytesPerGigabyte / 2, ShippedPricePerGigabyteRub);

        Assert.Equal(50.00m, amount);
    }

    /// <summary>Several gigabytes plus a fraction, at a price the platform owner changed - the shape a
    /// real invoice line takes. 2.5 GiB at 40 RUB/GiB is 100 RUB.</summary>
    [Fact]
    public void ComputeOverageRub_ScalesWithBothGigabytesAndTheOwnersOwnPrice()
    {
        var amount = DownloadOveragePricing.ComputeOverageRub(
            (long)(DownloadOveragePricing.BytesPerGigabyte * 2.5m), pricePerGigabyteRub: 40m);

        Assert.Equal(100.00m, amount);
    }

    /// <summary>Rounded to the kopeck, away from zero - the identical rounding
    /// `PurchaseAdministratorSlotHandler`'s own proration uses. One mebibyte at 100 RUB/GiB is
    /// 0.09765625 RUB, which must land on 0.10 and not be truncated to 0.09.</summary>
    [Fact]
    public void ComputeOverageRub_RoundsToTheKopeck_AwayFromZero()
    {
        var amount = DownloadOveragePricing.ComputeOverageRub(1024L * 1024L, ShippedPricePerGigabyteRub);

        Assert.Equal(0.10m, amount);
    }

    /// <summary>Not over the threshold at all, or exactly on it - nothing to pay, never a negative
    /// charge. This codebase has no refund mechanism for one to mean anything.</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-1024L * 1024L * 1024L)]
    public void ComputeOverageRub_IsZero_WhenNothingIsPastTheThreshold(long bytesOverThreshold)
    {
        Assert.Equal(0m, DownloadOveragePricing.ComputeOverageRub(bytesOverThreshold, ShippedPricePerGigabyteRub));
    }

    /// <summary>A gigabyte here is binary, not decimal - this type's own remarks on why mixing the two
    /// would make "how far over am I" and "what does that cost" disagree by 7%. Asserted as a fact of
    /// the constant rather than inferred from an amount, so a change to it fails here first.</summary>
    [Fact]
    public void BytesPerGigabyte_IsTheBinaryGigabyte_MatchingEveryThresholdItIsMeasuredAgainst()
    {
        Assert.Equal(1_073_741_824L, DownloadOveragePricing.BytesPerGigabyte);
    }

    /// <summary>`docs/backlog/25-84-*.md`'s own first Done-when: the price is the platform owner's own
    /// published data, and the only thing code registers is the key. A key nothing registers can never
    /// have a version published for it (`PricedResourceKeys.IsKnown` is what
    /// `PublishPriceVersionHandler` checks), so this registration is what makes the price
    /// owner-configurable at all.</summary>
    [Fact]
    public void OveragePerGigabyteKey_IsRegistered_SoTheOwnerCanPublishAPriceForIt()
    {
        Assert.True(PricedResourceKeys.IsKnown(DownloadOveragePricing.OveragePerGigabyteKey));
    }
}
