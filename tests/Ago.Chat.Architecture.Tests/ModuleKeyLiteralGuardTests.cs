namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `20-07`: guard 2 - see <see cref="ModuleKeyLiteralRule"/>'s own remarks for the mechanics and how
/// it differs from guard 1 (<see cref="MessageOpacityRule"/>).
/// </summary>
public class ModuleKeyLiteralGuardTests
{
    /// <summary>The real check: every <c>.cs</c> file this checkout actually ships is free of a
    /// literal matching <see cref="KnownModuleKeys.All"/>.</summary>
    [Fact]
    public void NoSourceFile_ContainsAKnownModuleKeyLiteral()
    {
        var srcDirectory = SourceTreeLocator.FindSrcDirectory();

        var violations = ModuleKeyLiteralRule.ScanSourceTree(srcDirectory, KnownModuleKeys.All);

        Assert.True(
            violations.Count == 0,
            "Ago.Chat.* must not contain a string literal of a known module key - a module's own "
            + "vocabulary reaching Ago.Chat.* as a literal is the boundary crossing the IL scan (guard 1) "
            + "cannot see (`if (moduleKey == \"calendar\")` compiles to a string, not a type reference). "
            + "Found: " + string.Join("; ", violations));
    }

    /// <summary>
    /// <b>The rule, proven able to fail</b> - the same permanent-fixture technique
    /// <see cref="MessageOpacityTests.TheRule_FlagsAMessageModelThatGrewBookingShapedFields"/> uses, and
    /// for the identical reason: a rule that has only ever been observed passing is not evidence.
    ///
    /// <para>Uses a scratch temp file rather than a checked-in fixture (unlike guard 1's
    /// <c>Fixtures.BoundaryCrossingMessageContent</c>) because this guard's own unit of work is a
    /// source <em>file</em>, not a compiled type - a real file on disk is what
    /// <see cref="ModuleKeyLiteralRule.ScanFile"/> actually reads, so proving it can fail means giving
    /// it a real file, not a string held in memory that never went through a path this rule's own
    /// production call (<see cref="ModuleKeyLiteralRule.ScanSourceTree"/>) would ever look at.</para>
    /// </summary>
    [Fact]
    public void TheRule_FlagsAStringLiteralOfAKnownModuleKey()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ModuleKeyLiteralGuardTests_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(tempFile, """
                namespace Scratch;

                public static class Scratch
                {
                    public static bool IsCalendar(string moduleKey) => moduleKey == "calendar";
                }
                """);

            var violations = ModuleKeyLiteralRule.ScanFile(tempFile, KnownModuleKeys.All);

            var violation = Assert.Single(violations);
            Assert.Equal("calendar", violation.Literal);
            Assert.Equal(tempFile, violation.FilePath);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>The negative case: an unrelated string literal (including one that merely *contains*
    /// the word, per this rule's own whole-literal-match design) does not trip the guard.</summary>
    [Fact]
    public void TheRule_DoesNotFlagAnUnrelatedOrPartialLiteral()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ModuleKeyLiteralGuardTests_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(tempFile, """
                namespace Scratch;

                public static class Scratch
                {
                    public const string Description = "This deployment's calendar-adjacent scheduling notes";
                    public const string Other = "choice_list";
                }
                """);

            var violations = ModuleKeyLiteralRule.ScanFile(tempFile, KnownModuleKeys.All);

            Assert.Empty(violations);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
