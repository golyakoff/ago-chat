using Mono.Cecil;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `14-06`: the boundary review's one addition, held as a rule that fails automatically instead of by
/// review - the same shape `0-02` gave the layering rules and `17-01` gave tenant scoping.
///
/// <para><b>What is being protected.</b> A message can carry a kind, an opaque payload and actions
/// so that a product - AGO Calendar, first - can put something interactive into a conversation that
/// works over a widget, Telegram, MAX or SMS alike. That only stays a boundary-preserving design for
/// as long as AGO Chat never learns what is inside. The moment it does, one product depends on
/// another product's domain, which is exactly what the repository split exists to prevent and is the
/// scenario <c>reviews/2026-08-26-platform-boundary.md</c> names as the thing that would prove its
/// own conclusion wrong: <i>"AGO Chat's message model gaining booking-shaped fields. If that ever
/// looks like the easy path, the boundary is being crossed and this document is the place that said
/// so in advance."</i></para>
///
/// <para>The mechanics, and why they are what they are, are in
/// <see cref="MessageOpacityRule"/>.</para>
/// </summary>
public class MessageOpacityTests
{
    [Fact]
    public void NoProductAssembly_NamesAnotherProductsDomain()
    {
        var violations = TestAssemblies.EveryChatAssembly
            .SelectMany(assembly => MessageOpacityRule
                .Scan(assembly.Cecil, MessageOpacityRule.ForeignDomainWords)
                .Select(v => (Key: $"{assembly.Name}: {v.Key}", Detail: $"{assembly.Name}: {v}")))
            .Where(v => !MessageOpacityExemptions.IsExempt(v.Key))
            .Select(v => v.Detail)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Ago.Chat.* must not name another product's domain - structured message content is opaque, and a "
            + "type, field, constant or branch that names what is inside it is the product-to-product dependency "
            + "the repository split exists to prevent (docs/reviews/2026-08-26-platform-boundary.md). Found: "
            + string.Join("; ", violations));
    }

    [Fact]
    public void NeitherDomainNorContracts_NamesASlotOrAService()
    {
        // The two words that cannot be enforced product-wide, enforced where they can be. Domain and
        // Contracts are where a violation would actually land - a field on Message, a field on a DTO -
        // and neither contains DI vocabulary, which is what makes "service" a usable signal here and
        // nowhere else. See MessageOpacityRule.
        var violations = new[] { TestAssemblies.Domain, TestAssemblies.Contracts }
            .SelectMany(assembly => MessageOpacityRule
                .Scan(assembly.Cecil, MessageOpacityRule.InnerLayerWords)
                .Select(v => (Key: $"{assembly.Name}: {v.Key}", Detail: $"{assembly.Name}: {v}")))
            .Where(v => !MessageOpacityExemptions.IsExempt(v.Key))
            .Select(v => v.Detail)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Ago.Chat.Domain and Ago.Chat.Contracts must not name a slot or a service: a booking's vocabulary "
            + "reaching the message model or the wire contract is the boundary crossing this rule exists to "
            + "catch. Found: " + string.Join("; ", violations));
    }

    /// <summary>
    /// <b>The rule, proven able to fail.</b> The same permanent-fixture technique
    /// <see cref="TenantScopeTests.TheRule_FlagsAHandlerThatTakesASiteIdAndNeverChecksPermission"/>
    /// uses, and for the same reason: a rule that has only ever been observed passing is not
    /// evidence.
    ///
    /// <para><see cref="Fixtures.BoundaryCrossingMessageContent"/> is exactly the shape this item
    /// exists to make unnecessary - a message model that grew booking-shaped fields, a kind enum with
    /// a member per product concept, and a branch comparing a content kind against a literal. Its
    /// compliant twin sits beside it so that a rule which flagged everything would fail here
    /// too.</para>
    /// </summary>
    [Fact]
    public void TheRule_FlagsAMessageModelThatGrewBookingShapedFields()
    {
        var found = MessageOpacityRule.Scan(OwnAssembly(), MessageOpacityRule.ForeignDomainWords)
            .Where(v => v.Where.StartsWith("Ago.Chat.Architecture.Tests.Fixtures.", StringComparison.Ordinal))
            .ToList();

        var violating = found
            .Where(v => v.Where.Contains("BoundaryCrossing", StringComparison.Ordinal))
            .ToList();

        // A field, an enum member, and a string literal in a branch - the three shapes the reviewer
        // listed, each caught by a different arm of the scanner.
        Assert.Contains(violating, v => v.Kind == "property");
        Assert.Contains(violating, v => v.Kind == "enum member");
        Assert.Contains(violating, v => v.Kind == "string literal");

        // And the compliant twin is untouched, so the rule is not simply flagging everything in the
        // fixtures namespace.
        Assert.DoesNotContain(found, v => v.Where.Contains("OpaqueMessageContent", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRule_MatchesWholeWordsAndNotSubstrings()
    {
        // "order preservation" contains "reservation", and a comment on main already says exactly
        // that. A substring rule would have failed against code that was already merged, which is how
        // a rule gets an exemption list instead of a fix.
        Assert.DoesNotContain("reservation", MessageOpacityRule.Words("PreservationPolicy"));
        Assert.Contains("reservation", MessageOpacityRule.Words("reservation_id"));
        Assert.Equal(["slot", "picker"], MessageOpacityRule.Words("SlotPicker"));
        Assert.Equal(["calendar", "slot", "picker"], MessageOpacityRule.Words("calendar.slot_picker"));

        // Digits stay attached to the word they follow, so a UTF-8 helper does not become "utf".
        Assert.Equal(["utf8", "reader"], MessageOpacityRule.Words("Utf8Reader"));
    }

    /// <summary>
    /// The other direction, and the one that keeps the exemption list honest - the same staleness
    /// check <see cref="TenantScopeTests.NoExemption_IsStale"/> makes. An exemption for something
    /// that has since been renamed or removed is a claim sitting in a file whose entire value is that
    /// a reviewer can trust what it says.
    /// </summary>
    [Fact]
    public void NoExemption_IsStale()
    {
        var live = TestAssemblies.EveryChatAssembly
            .SelectMany(a => MessageOpacityRule.Scan(a.Cecil, MessageOpacityRule.ForeignDomainWords)
                .Select(v => $"{a.Name}: {v.Key}"))
            .Concat(new[] { TestAssemblies.Domain, TestAssemblies.Contracts }
                .SelectMany(a => MessageOpacityRule.Scan(a.Cecil, MessageOpacityRule.InnerLayerWords)
                    .Select(v => $"{a.Name}: {v.Key}")))
            .ToHashSet(StringComparer.Ordinal);

        var stale = MessageOpacityExemptions.ByViolation.Keys
            .Where(key => !live.Contains(key))
            .ToList();

        Assert.True(
            stale.Count == 0,
            "Stale entries in MessageOpacityExemptions - the code they excuse no longer exists: "
            + string.Join("; ", stale));
    }

    private static AssemblyDefinition OwnAssembly() =>
        AssemblyDefinition.ReadAssembly(
            Path.Combine(AppContext.BaseDirectory, "Ago.Chat.Architecture.Tests.dll"));
}
