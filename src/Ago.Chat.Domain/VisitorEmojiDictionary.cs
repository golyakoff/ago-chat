namespace Ago.Chat.Domain;

/// <summary>
/// `25-56`: the two fixed, shipped emoji sets a visitor's own <see cref="Visitor.EmojiCreature"/>/
/// <see cref="Visitor.EmojiFood"/> pair is drawn from - the author's own decision 3: "pick two lists
/// (creatures, food), each with enough neutral, visually-distinct members that combinations don't
/// repeat constantly for a small tenant. Not a per-tenant configurable list; this is a memory aid, not
/// a branding surface." Both lists live here, in Domain, rather than beside the code that picks from
/// them (<c>VisitorEmojiPairGenerator</c>, Infrastructure) - which two emoji groups exist and exactly
/// which members belong to each is a business rule this system enforces (decision 2: "two emoji, from
/// two different groups"), not an implementation detail of how randomness is drawn. Keeping the
/// dictionaries here also means a domain test can assert an assigned pair is one of these members
/// without reaching into Infrastructure at all.
///
/// <para><b>Twenty members each, not the fifteen-to-thirty range's floor or ceiling.</b> 20 x 20 = 400
/// distinct combinations - decision 4 already accepts that a new visitor may repeat a combination
/// another visitor already has ("this is not a uniqueness guarantee, it is a mnemonic"), so this list
/// only has to make a repeat feel occasional rather than routine for a small tenant's own population of
/// concurrently-open conversations, which 400 comfortably does.</para>
///
/// <para><b>Why these particular members.</b> Every entry is a real, common emoji glyph a console
/// operator would recognise on sight (no rare or ambiguous-rendering codepoints), each visually and
/// semantically distinct from every other member of its own list (no near-duplicates like both a
/// generic "cat face" and a "grinning cat" that would read as the same mnemonic at a glance), and
/// deliberately neutral - decision 3 rules out anything branded or tenant-specific.
/// <see cref="Creatures"/> mixes land animals, birds, and sea creatures (the author's own examples span
/// all three: a chicken, a fish, a whale) rather than only mammals, which is what buys the visual
/// distinctiveness a memory aid needs. <see cref="Foods"/> is exclusively food and drink, matching the
/// same three examples (an orange, a kiwi, a hot dog).</para>
///
/// <para><b>Frozen once shipped, in spirit if not in the type system.</b> Nothing here stops a future
/// change from editing these arrays, but doing so never touches an already-assigned visitor - decision
/// 5's "permanent for that visitor" is enforced by <see cref="Visitor.AssignEmojiPair"/> guarding
/// against reassignment, not by these lists being literally immutable. The backfill migration
/// (`Stage25AddVisitorEmojiPair`) copies these exact members into a one-time SQL literal at the moment
/// it was authored; editing this list afterward does not retroactively change that migration, which is
/// correct - a migration already applied to a real database is never edited (`db-migration` skill).
/// </para>
/// </summary>
public static class VisitorEmojiDictionary
{
    public static readonly IReadOnlyList<string> Creatures =
    [
        "🐔", "🐠", "🐳", "🐶", "🐱", "🐭", "🐹", "🐰", "🦊", "🐻",
        "🐼", "🐨", "🐯", "🦁", "🐮", "🐷", "🐸", "🐵", "🐦", "🦉",
    ];

    public static readonly IReadOnlyList<string> Foods =
    [
        "🍊", "🥝", "🌭", "🍕", "🍔", "🍟", "🌮", "🍣", "🍩", "🍪",
        "🍦", "🍎", "🍌", "🍇", "🍉", "🍓", "🍒", "🍑", "🥑", "🍍",
    ];
}
