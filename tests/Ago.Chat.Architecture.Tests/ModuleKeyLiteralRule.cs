using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `20-07`/`adr/0065` §9's third guard: "a check for string literals of known module keys inside
/// <c>Ago.Chat.*</c>. The IL scan cannot see it, and it is the cheapest way to shortcut the whole
/// design under time pressure." Deliberately a <em>different technique</em> from guard 1
/// (<see cref="MessageOpacityRule"/>, a Mono.Cecil IL scan), not a second copy of it, per this item's
/// own instruction - and the difference is not cosmetic:
///
/// <list type="bullet">
/// <item><b>Source, not IL.</b> Guard 1 reads compiled metadata (types, members, <c>ldstr</c>
/// operands); this reads the <c>.cs</c> files themselves via a Roslyn syntax tree, with no compilation
/// step at all - a plain string literal token is syntactically identifiable without ever needing a
/// semantic model, a project reference graph, or a successful build.</item>
/// <item><b>A maintained allow-list of real registered keys, not a curated English-word list.</b> Guard
/// 1's <c>ForeignDomainWords</c> is a set of words a human judged to belong to another product's domain
/// - it catches <c>"calendar"</c> because a reviewer recognised the English word. Guard 2's
/// <see cref="KnownModuleKeys"/> is the literal set of keys this deployment actually wires statically
/// (`adr/0065` §8) - it would catch a future module key that is <em>not</em> an obvious domain word (an
/// opaque code name, an abbreviation, anything a curated word list would never think to include),
/// because it does not need to recognise the word at all, only match it exactly.</item>
/// </list>
///
/// <para><b>Exact, whole-literal matching - not the word-splitting guard 1 uses.</b> A
/// <see cref="Domain.ModuleKey"/> is a single flat token compared as a whole (<c>TriggerCommandMatcher</c>'s
/// own exact-match reasoning), never a compound identifier to split on case transitions - so unlike
/// guard 1's <c>Words()</c> helper, this rule compares a literal's entire value against the allow-list,
/// which is also what keeps it from flagging a sentence that merely contains the word (a comment
/// explaining what "calendar" is, quoted in a string for a log message, would not be the literal
/// <c>"calendar"</c> alone).</para>
///
/// <para><b>Comments and non-string tokens are invisible, the same way guard 1 cannot see them either</b>
/// - a Roslyn literal-expression node is only ever produced for an actual literal in code, never for
/// trivia.</para>
/// </summary>
internal static class ModuleKeyLiteralRule
{
    /// <summary><see cref="FilePath"/>/<see cref="Line"/> are what makes a failure actionable - the
    /// same "a failure message that says nothing actionable is worse than no test" reasoning guard 1's
    /// own <c>Violation.Detail</c> exists for.</summary>
    internal sealed record Violation(string FilePath, int Line, string Literal)
    {
        public override string ToString() => $"{FilePath}:{Line}: string literal \"{Literal}\"";
    }

    /// <summary>Scans every <c>.cs</c> file under <paramref name="rootDirectory"/>, recursively,
    /// excluding <c>bin</c>/<c>obj</c> build output - a source-tree scan has no equivalent of guard 1's
    /// "compiler-generated types" exclusion, because generated <em>files</em> (EF migration
    /// <c>.Designer.cs</c> included) are ordinary source text Roslyn parses the same as anything
    /// else, and a migration that somehow named a module key literally would be exactly the kind of
    /// thing this guard should catch too.</summary>
    public static IReadOnlyList<Violation> ScanSourceTree(string rootDirectory, IReadOnlySet<string> knownModuleKeys)
    {
        var violations = new List<Violation>();

        foreach (var filePath in Directory.EnumerateFiles(rootDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(filePath))
            {
                continue;
            }

            violations.AddRange(ScanFile(filePath, knownModuleKeys));
        }

        return violations;
    }

    /// <summary>The unit the fails-before proof (<c>ModuleKeyLiteralGuardTests</c>) actually exercises -
    /// scanning one file directly, with no filesystem enumeration, so the test needs no scratch
    /// directory tree.</summary>
    public static IReadOnlyList<Violation> ScanFile(string filePath, IReadOnlySet<string> knownModuleKeys)
    {
        var text = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(text, path: filePath);
        var root = tree.GetRoot();

        var violations = new List<Violation>();
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }

            var value = literal.Token.ValueText;
            if (knownModuleKeys.Contains(value))
            {
                var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                violations.Add(new Violation(filePath, line, value));
            }
        }

        return violations;
    }

    private static bool IsBuildOutput(string filePath) =>
        filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
