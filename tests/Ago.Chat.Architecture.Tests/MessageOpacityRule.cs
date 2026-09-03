using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `14-06`: the mechanical half of the opacity guard - given an assembly, find every place
/// <c>Ago.Chat.*</c> has named another product's domain.
///
/// <para><b>The property, in the reviewer's own words:</b> <i>`Ago.Chat.*` contains no type, field,
/// constant or branch naming a booking, a slot, or a service.</i>
/// (<c>reviews/2026-08-26-platform-boundary.md</c>.) Structured message content is what makes a
/// booking reachable from a conversation without AGO Chat knowing what one is; the moment AGO Chat
/// *does* know, the repository split has been defeated through a data model instead of a
/// <c>ProjectReference</c>, which is the failure nobody would notice.</para>
///
/// <para><b>Compiled metadata, not source text.</b> The scan reads type, member and parameter names,
/// enum members and <c>ldstr</c> string literals out of IL. Comments are therefore invisible to it -
/// deliberately. A comment cannot create a dependency, and this codebase explains itself in prose
/// heavily enough that a source-text rule would be unusable: <c>IOperatorCapacity</c> talks about
/// reserving a "slot" of operator capacity, which is a different concept with the same English name.
/// A <i>field</i> named for one, or a <i>literal</i> compared against one, is what this catches.</para>
///
/// <para><b>Word-level matching, not substring.</b> Identifiers are split on case transitions and
/// non-alphanumerics before comparison, so <c>PreservationPolicy</c> does not read as
/// <c>reservation</c> and <c>"deliver-to-connections"</c> does not read as anything at all. A
/// substring rule would have produced exactly that false positive against code already on
/// <c>main</c>.</para>
///
/// <para><b>Two tiers, because one word is unusable in one of them.</b> <c>service</c> is the term
/// the reviewer named that .NET's own dependency-injection vocabulary saturates -
/// <c>IServiceProvider</c>, <c>IServiceCollection</c>, <c>services</c>, <c>AddScoped</c>. Flagging it
/// everywhere would produce an exemption list longer than the rule, and a list that long is one
/// nobody reads. So it is enforced where DI vocabulary cannot legitimately appear -
/// <c>Ago.Chat.Domain</c> and <c>Ago.Chat.Contracts</c>, the two assemblies a boundary violation
/// would actually land in, because a violation is a field on a message or a field on a DTO. Verified
/// empirically before choosing the split: neither assembly contains the word today, in any
/// form.</para>
/// </summary>
internal static class MessageOpacityRule
{
    /// <summary>
    /// Enforced in every <c>Ago.Chat.*</c> assembly, hosts included. None of these words has an
    /// innocent meaning anywhere in a chat product.
    /// </summary>
    public static readonly IReadOnlySet<string> ForeignDomainWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "booking",
        "bookings",
        "book",
        "appointment",
        "appointments",
        "reservation",
        "reservations",
        "calendar",
        "calendars",
        "practitioner",
    };

    /// <summary>
    /// <b>Words considered and left out, because why a word is absent is as much a part of this rule
    /// as why one is present.</b>
    ///
    /// <para><c>worker</c> - a booking product's central noun, and unusable here. In a .NET service
    /// "worker" means a background thread and a deployable: <c>Ago.Chat.Worker</c> is one of
    /// <c>adr/0013</c>'s three hosts, <c>MessagePipelineWorkerHost</c> runs the send pipeline, and
    /// <c>MessagePipelineOptions.WorkerCount</c> sizes it. Including it produced seven hits and not
    /// one of them was a boundary crossing, which is the definition of a signal that carries no
    /// information. Checked by running it, not assumed.</para>
    ///
    /// <para><c>customer</c> - AGO Chat legitimately has customers in the commercial sense, and the
    /// word would collide with `8-07`'s demo tenants and every piece of prose about who is being
    /// sold to.</para>
    ///
    /// <para>A rule that flags things nobody will act on gets an exemption list longer than itself,
    /// and then gets deleted. Two words earn their place product-wide; two more earn it in the two
    /// assemblies where a violation would actually land.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> DeliberatelyNotEnforced = ["worker", "customer"];

    /// <summary>
    /// `22-05`/`adr/0093`: one type, deliberately outside this rule's own reach - not a
    /// <see cref="MessageOpacityExemptions"/> entry, because those exist for a coincidental word
    /// collision and this is the opposite: <c>Permission</c> now names AGO Calendar's own vocabulary
    /// (<c>booking:*</c>, <c>calendar:*</c>) on purpose. `adr/0027`'s "two RBAC vocabularies from day
    /// one, never overlapping" is the clause `adr/0093` retired - the account side's role catalogue
    /// unifies both products' permission strings into one type, precisely so a person's grant of
    /// <c>calendar:configure</c> is a fact this repository can hold and replicate outward
    /// (<c>RoleAssignmentsChanged</c>) rather than something it must stay ignorant of.
    ///
    /// <para><b>What this does not weaken.</b> The property this whole file protects - structured
    /// message content staying opaque, so a booking is reachable from a conversation without AGO Chat
    /// knowing what one is - is untouched. A permission string granted or checked is not message
    /// content, and nothing about this exemption lets <c>Message</c>, <c>MessageDto</c> or any wire
    /// contract grow a booking-shaped field; <see cref="InnerLayerWords"/>'s own scan of
    /// <c>Ago.Chat.Domain</c>/<c>Ago.Chat.Contracts</c> for <c>slot</c>/<c>service</c> is untouched by
    /// this list, and still would catch it.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ExemptTypeFullNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Ago.Chat.Domain.Permission",
    };

    /// <summary>
    /// Enforced only in <c>Ago.Chat.Domain</c> and <c>Ago.Chat.Contracts</c> - see this class's own
    /// remarks for why these two words cannot be enforced product-wide and why these two assemblies
    /// are the ones that matter.
    /// </summary>
    public static readonly IReadOnlySet<string> InnerLayerWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "slot",
        "slots",
        "service",
        "services",
    };

    /// <summary>
    /// One place a forbidden word was found.
    ///
    /// <para><see cref="Key"/> and <see cref="Detail"/> are separate on purpose. An exemption matches
    /// on the key, which names <i>where</i> and <i>which word</i> and nothing else - so an argued
    /// exemption for a metric description survives that description being reworded, instead of going
    /// stale for a change that has nothing to do with the boundary. <see cref="Detail"/> adds the
    /// offending text and is what a failure message prints, because "something matched" is not an
    /// actionable failure.</para>
    /// </summary>
    internal sealed record Violation(string Where, string Kind, string Member, string Detail, string Word)
    {
        /// <summary>Stable across a rewording of the offending text; changes when the code moves.</summary>
        public string Key => $"{Where} -> {Kind} in '{Member}' names '{Word}'";

        public override string ToString() => Detail.Length == 0
            ? Key
            : $"{Where} -> {Kind} {Detail} in '{Member}' names '{Word}'";
    }

    public static IReadOnlyList<Violation> Scan(AssemblyDefinition assembly, IReadOnlySet<string> words)
    {
        var violations = new List<Violation>();

        foreach (var type in assembly.MainModule.GetTypes())
        {
            // Compiler-generated types carry their originating member's name mangled into their own
            // (<HandleAsync>d__7), so scanning them would double-report whatever the real member
            // already reports - and their *fields* are the hoisted locals of that member, which is
            // genuinely the same code.
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            // `22-05`/`adr/0093`: see ExemptTypeFullNames' own remarks - one type, deliberately
            // excluded from this scan for a real, ADR-recorded reason rather than a coincidental
            // word collision.
            if (ExemptTypeFullNames.Contains(type.FullName))
            {
                continue;
            }

            Check(violations, type.FullName, "type name", type.Name, string.Empty, type.Name, words);

            foreach (var field in type.Fields)
            {
                // An enum member is a field with a constant value - the shape a "kind" enum would
                // take if somebody ever added one, which is precisely the violation this item's
                // MessageContentKind exists to make unnecessary.
                var memberKind = type.IsEnum && field.HasConstant ? "enum member" : "field";
                Check(violations, type.FullName, memberKind, field.Name, string.Empty, field.Name, words);
            }

            foreach (var property in type.Properties)
            {
                Check(violations, type.FullName, "property", property.Name, string.Empty, property.Name, words);
            }

            foreach (var method in type.Methods)
            {
                Check(violations, type.FullName, "method", method.Name, string.Empty, method.Name, words);

                foreach (var parameter in method.Parameters)
                {
                    Check(violations, type.FullName, "parameter", method.Name, $"'{parameter.Name}'", parameter.Name, words);
                }

                ScanLiterals(violations, type, method, words);
            }
        }

        return violations;
    }

    /// <summary>
    /// String literals, which is where the sneakiest form of this violation lives: not a field named
    /// for a booking but a <c>switch</c> or an <c>if</c> comparing a content kind against one. That
    /// is "a branch naming a booking" in the reviewer's list, and it is invisible to any rule that
    /// only looks at member names.
    /// </summary>
    private static void ScanLiterals(
        List<Violation> violations, TypeDefinition type, MethodDefinition method, IReadOnlySet<string> words)
    {
        if (!method.HasBody)
        {
            return;
        }

        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code == Code.Ldstr && instruction.Operand is string literal)
            {
                Check(
                    violations, type.FullName, "string literal", method.Name,
                    $"\"{Truncate(literal)}\"", literal, words);
            }
        }
    }

    private static void Check(
        List<Violation> violations, string where, string kind, string member, string detail, string text,
        IReadOnlySet<string> words)
    {
        foreach (var word in Words(text))
        {
            if (words.Contains(word))
            {
                violations.Add(new Violation(where, kind, member, detail, word));
                return;
            }
        }
    }

    /// <summary>Splits an identifier or a literal into lowercase words: on case transitions
    /// (<c>SlotPicker</c>) and on anything that is not a letter or a digit
    /// (<c>calendar.slot_picker</c>). Digits attach to the word they follow, so <c>Utf8</c> stays one
    /// word rather than becoming a bare <c>utf</c>.</summary>
    internal static IEnumerable<string> Words(string text)
    {
        var current = new System.Text.StringBuilder();

        foreach (var character in text)
        {
            if (!char.IsLetterOrDigit(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString().ToLowerInvariant();
                    current.Clear();
                }

                continue;
            }

            if (char.IsUpper(character) && current.Length > 0)
            {
                yield return current.ToString().ToLowerInvariant();
                current.Clear();
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString().ToLowerInvariant();
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : value[..60] + "...";

    private static bool IsCompilerGenerated(TypeDefinition type) =>
        type.Name.Contains('<', StringComparison.Ordinal)
        || type.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute");
}
