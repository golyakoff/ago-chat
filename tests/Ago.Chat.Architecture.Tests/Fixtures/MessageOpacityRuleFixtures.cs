namespace Ago.Chat.Architecture.Tests.Fixtures;

/// <summary>
/// <b>Deliberately violating.</b> The message model `14-06` exists to make unnecessary: fields named
/// for another product's domain, a kind enum with a member per product concept, and a branch
/// comparing a content kind against a known literal.
///
/// <para>It lives here, permanently and in the build, so that
/// <see cref="MessageOpacityTests.TheRule_FlagsAMessageModelThatGrewBookingShapedFields"/> can show
/// the rule failing rather than only passing - `0-02` demonstrated its layering rules by violating
/// them, and `17-01` did the same for tenant scoping. This type is never referenced by product code
/// and its assembly is never in <see cref="TestAssemblies.EveryChatAssembly"/>, so it cannot turn
/// the real rule red.</para>
///
/// <para>If this file ever stops looking absurd, the boundary has moved.</para>
/// </summary>
internal sealed class BoundaryCrossingMessageContent
{
    // A field naming another product's domain - the shape the review calls out by name.
    public string? BookingReference { get; set; }

    public DateTimeOffset AppointmentStartsAt { get; set; }

    public BoundaryCrossingContentKind Kind { get; set; }

    /// <summary>A branch on a known vocabulary word: the sneakiest form, and the one no rule that
    /// only reads member names would ever see.</summary>
    public bool IsSomethingThisProductShouldNotUnderstand(string contentKind) =>
        contentKind == "calendar.slot_picker";
}

/// <summary>A closed set of another product's concepts - exactly what <c>MessageContentKind</c> is a
/// string instead of.</summary>
internal enum BoundaryCrossingContentKind
{
    None = 0,
    BookingOffer = 1,
    AppointmentConfirmed = 2,
}

/// <summary>
/// The compliant twin, so that a rule which flagged everything in this namespace would fail its own
/// demonstration. Structurally identical - a kind, a payload, a branch - and it names nothing,
/// because there is nothing for it to name.
/// </summary>
internal sealed class OpaqueMessageContent
{
    public string? Kind { get; set; }

    public string? Payload { get; set; }

    public IReadOnlyList<string> Labels { get; set; } = [];

    /// <summary>The only question this product ever asks about structured content: is there any.</summary>
    public bool HasStructure() => !string.IsNullOrEmpty(Kind);
}
