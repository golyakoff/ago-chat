using System.Text.Json;
using Ago.Chat.Api.Billing;
using Ago.Chat.Application.UseCases.GetBillingStatus;
using Ago.Chat.Domain;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-304`: the wire-serialization gap `26-299` left untested - none of
/// <see cref="BillingEndpoints.PurchaseChannelAddOnRequest"/>/<see cref="BillingEndpoints.PreviewBillingPurchaseRequest"/>/
/// <see cref="BillingConnectedChannelDto"/> had a test asserting what actually crosses the wire for their
/// own enum-shaped fields. Before this item, all three bound/emitted the bare <see cref="Domain.ChannelKind"/>
/// or <see cref="Application.UseCases.PreviewBillingPurchase.BillingPurchaseKind"/> enum directly -
/// `System.Text.Json` serializes an enum with no converter registered as its numeric ordinal (e.g.
/// <c>"kind":2</c>, not <c>"kind":"Channel"</c>) - fragile (any reorder of that enum silently shifts the
/// wire value) and inconsistent with the "a client reads a name, not an ordinal" convention the rest of
/// this codebase's own <see cref="Domain.ChannelKind"/>-on-wire surface already uses (see e.g.
/// <c>Ago.Chat.Api.ChannelIdentities.ChannelIdentityEndpoints.ChannelIdentityDto.Kind</c>,
/// <see cref="BillingSubscriptionSummaryDto.Status"/>).
///
/// <para><b><see cref="JsonSerializerDefaults.Web"/>, not a running host</b> - the identical scope-and-limit
/// <c>UpdateWidgetConfigRequestBindingTests</c>'s own remarks already state: this is what Minimal API binds
/// a request body with, and <c>Program.cs</c> configures no JSON options at all, so this is the real
/// contract without needing a running host.</para>
/// </summary>
public sealed class BillingWireEnumSerializationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PurchaseChannelAddOnRequest_ChannelKind_BindsFromAndSerializesTo_TheMemberNameString()
    {
        const string body = """{ "channelKind": "Telegram" }""";

        var request = JsonSerializer.Deserialize<BillingEndpoints.PurchaseChannelAddOnRequest>(body, WebOptions);

        Assert.NotNull(request);
        Assert.Equal(nameof(ChannelKind.Telegram), request.ChannelKind);

        AssertStringProperty(JsonSerializer.SerializeToDocument(request, WebOptions), "channelKind", "Telegram");
    }

    [Fact]
    public void PreviewBillingPurchaseRequest_KindAndChannelKind_BindFromAndSerializeTo_TheMemberNameStrings()
    {
        const string body = """
            {
              "kind": "Channel",
              "requestedSeats": null,
              "requestedExtraAdministrators": null,
              "channelKind": "Max"
            }
            """;

        var request = JsonSerializer.Deserialize<BillingEndpoints.PreviewBillingPurchaseRequest>(body, WebOptions);

        Assert.NotNull(request);
        Assert.Equal("Channel", request.Kind);
        Assert.Equal("Max", request.ChannelKind);

        var document = JsonSerializer.SerializeToDocument(request, WebOptions);
        AssertStringProperty(document, "kind", "Channel");
        AssertStringProperty(document, "channelKind", "Max");
    }

    /// <summary>`ChannelKind` stays optional on this wire exactly as before this item - only its
    /// representation (string, not enum) changed. A `Seats`/`Administrators` preview sends no channel
    /// kind at all, and that must still bind to <see langword="null"/>, not fail or default to some
    /// member.</summary>
    [Fact]
    public void PreviewBillingPurchaseRequest_WithNoChannelKind_BindsNull_ForASeatsPreview()
    {
        const string body = """
            {
              "kind": "Seats",
              "requestedSeats": 10,
              "requestedExtraAdministrators": null,
              "channelKind": null
            }
            """;

        var request = JsonSerializer.Deserialize<BillingEndpoints.PreviewBillingPurchaseRequest>(body, WebOptions);

        Assert.NotNull(request);
        Assert.Equal("Seats", request.Kind);
        Assert.Null(request.ChannelKind);
    }

    [Fact]
    public void BillingConnectedChannelDto_Kind_SerializesAndRoundTrips_AsTheMemberNameString()
    {
        var dto = new BillingConnectedChannelDto(
            Kind: ChannelKind.Telegram.ToString(),
            SubscriptionId: Guid.NewGuid(),
            CancelRequested: false,
            CurrentPeriodEnd: null);

        var document = JsonSerializer.SerializeToDocument(dto, WebOptions);
        AssertStringProperty(document, "kind", "Telegram");

        var json = document.RootElement.GetRawText();
        var roundTripped = JsonSerializer.Deserialize<BillingConnectedChannelDto>(json, WebOptions);
        Assert.Equal("Telegram", roundTripped!.Kind);
    }

    /// <summary>Fails a numeric ordinal (`JsonValueKind.Number`) exactly as hard as it fails a wrong
    /// string - this is the assertion that would have caught `26-299`'s own gap: before this item, every
    /// one of the three call sites above serialized its enum-shaped field with `JsonValueKind.Number`,
    /// not `String`, because no `JsonStringEnumConverter` was registered anywhere on this wire
    /// (`Ago.Chat.Contracts.WireJsonOptions`'s own remarks on why one is deliberately not added
    /// globally).</summary>
    private static void AssertStringProperty(JsonDocument document, string propertyName, string expected)
    {
        var property = document.RootElement.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.String, property.ValueKind);
        Assert.Equal(expected, property.GetString());
    }
}
