using Ago.Chat.Application.Emailing;

namespace Ago.Chat.Application.Tests.Emailing;

/// <summary>
/// `25-155`: <see cref="EmailHtmlShell"/>'s own unit coverage - the parts of its contract that do not need
/// a real SMTP send to prove (<see cref="Ago.Chat.Integration.Tests.NotificationMailSenderTests"/>'s own
/// scope covers the real-send half): every optional section is actually optional, and every caller-supplied
/// string is HTML-encoded rather than trusted as markup (<see cref="EmailShellContent"/>'s own remarks on
/// why - this shell's real callers all interpolate tenant-supplied data).
/// </summary>
public sealed class EmailHtmlShellTests
{
    [Fact]
    public void Render_IncludesTheHeadingAndEveryBodyParagraph()
    {
        var html = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "Welcome to the team",
            BodyParagraphs: ["First paragraph.", "Second paragraph."]));

        Assert.Contains("Welcome to the team", html);
        Assert.Contains("First paragraph.", html);
        Assert.Contains("Second paragraph.", html);
    }

    [Fact]
    public void Render_WithNoCallToAction_OmitsTheButtonEntirely()
    {
        var html = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "Heads up",
            BodyParagraphs: ["Just some information."]));

        Assert.DoesNotContain("roundrect", html);
        Assert.DoesNotContain("background-color:#2F6CE0", html); // the CTA button's own fill colour
    }

    [Fact]
    public void Render_WithACallToAction_IncludesTheLabelAndUrlInBothTheAnchorAndTheOutlookFallback()
    {
        var html = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "Heads up",
            BodyParagraphs: ["Just some information."],
            CallToAction: new EmailShellCallToAction("Open the console", "https://office.example.test/login")));

        Assert.Contains("Open the console", html);
        Assert.Contains("https://office.example.test/login", html);
        Assert.Contains("roundrect", html); // the Outlook VML fallback
    }

    [Fact]
    public void Render_WithNoSecondaryNote_OmitsTheNoteBlock()
    {
        var html = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "Heads up",
            BodyParagraphs: ["Just some information."]));

        Assert.DoesNotContain("&#10003;", html); // the note block's own checkmark icon
    }

    [Fact]
    public void Render_WithASecondaryNote_IncludesIt()
    {
        var html = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "Heads up",
            BodyParagraphs: ["Just some information."],
            SecondaryNote: "We are glad to have you."));

        Assert.Contains("We are glad to have you.", html);
        Assert.Contains("&#10003;", html);
    }

    /// <summary>Every one of this shell's real callers interpolates tenant-supplied data (a site's own
    /// name, an operator's own display name) into the heading or body paragraphs - so this shell must
    /// never trust that text as markup, the same reason `EmailShellContent`'s own remarks give.</summary>
    [Fact]
    public void Render_HtmlEncodesCallerSuppliedTextInsteadOfTrustingItAsMarkup()
    {
        var html = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "<script>alert(1)</script>",
            BodyParagraphs: ["Site name: <b>Acme</b> & co."],
            SecondaryNote: "Note with <i>tags</i>",
            CallToAction: new EmailShellCallToAction("<u>Click</u>", "https://example.test/?a=1&b=2")));

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<b>Acme</b>", html);
        Assert.DoesNotContain("<i>tags</i>", html);
        Assert.DoesNotContain("<u>Click</u>", html);
    }

    [Fact]
    public void Render_WithNoExplicitPreheader_DefaultsItToTheHeading()
    {
        var html = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "Your account will be deleted soon",
            BodyParagraphs: ["Details follow."]));

        Assert.Contains(
            "<div class=\"preheader\">Your account will be deleted soon</div>", html);
    }

    [Fact]
    public void Render_ProducesAWellFormedStandaloneHtmlDocument()
    {
        var html = EmailHtmlShell.Render(new EmailShellContent(
            Heading: "Heads up",
            BodyParagraphs: ["Just some information."]));

        Assert.StartsWith("<!DOCTYPE html", html);
        Assert.Contains("<html", html);
        Assert.Contains("</html>", html);
        Assert.Contains("AGO", html); // the fixed brand/logo row
    }
}
