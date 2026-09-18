using System.Net;
using System.Text;

namespace Ago.Chat.Application.Emailing;

/// <summary>
/// `25-155`: one clickable call to action inside <see cref="EmailShellContent"/> - a labelled button,
/// rendered with both a real HTML anchor (every mail client except Outlook desktop) and Outlook's own VML
/// <c>roundrect</c> fallback (<see cref="EmailHtmlShell"/>'s own remarks on why Outlook needs a second,
/// parallel rendering to get a styled button at all - the identical technique
/// `docs/backlog/25-155-email-template-mockup.html` already uses).
/// </summary>
public sealed record EmailShellCallToAction(string Label, string Url);

/// <summary>
/// `25-155`: everything one of this deployment's own transactional emails supplies to
/// <see cref="EmailHtmlShell"/> - the varying half of "logo row, hero icon, heading, body copy, one CTA,
/// a warm secondary note, muted footer", the shape `docs/backlog/25-155-*.md`'s own mockup gives every
/// trigger this deployment sends mail for. The logo row and the footer are deliberately not parameters:
/// they are this deployment's own fixed brand identity, identical on every mail this shell ever renders -
/// the same reason <see cref="Ago.Chat.Infrastructure.Email.EmailMimeMessageBuilder"/> never takes a
/// caller-supplied <c>From</c> display name either.
///
/// <para><paramref name="BodyParagraphs"/> and <paramref name="Heading"/> are plain text, HTML-encoded by
/// <see cref="EmailHtmlShell"/> itself - never raw HTML a caller hands in. Every caller of this shell
/// interpolates values that ultimately trace back to tenant-supplied data (a site's own name, an
/// operator's own display name), so encoding is not optional here the way it would be for a literal owned
/// by this shell alone.</para>
/// </summary>
public sealed record EmailShellContent(
    string Heading,
    IReadOnlyList<string> BodyParagraphs,
    EmailShellCallToAction? CallToAction = null,
    string? SecondaryNote = null,
    string HeroIcon = "\U0001F44B",
    string? Preheader = null);

/// <summary>
/// `25-155`: the one shared HTML "shell" every one of this deployment's own transactional emails renders
/// through - a direct port of `docs/backlog/25-155-email-template-mockup.html`'s own structure and inline
/// styles (table-based layout, every style inlined, MSO/VML conditionals for a real Outlook desktop
/// button, a hidden preheader, a <c>max-width:600px</c> fluid card), parametrized where that mockup's own
/// content varies per trigger and left fixed where it does not (`EmailShellContent`'s own remarks). Colour
/// tokens, fonts and spacing are copied from the mockup file itself, not from `docs/backlog/25-155-*.md`'s
/// own prose summary of it - that summary names <c>--bg:#F4F6FB</c> for the page background, but the
/// mockup file's own CSS actually paints <c>#eef1f8</c>; the file is what this item calls "a real,
/// complete, standalone HTML email" to reproduce, so its literal values win over a paraphrase of
/// them.
///
/// <para><b>Why this lives in <c>Ago.Chat.Application</c>, not <c>Ago.Chat.Infrastructure.Email</c>.</b>
/// The dependency rule (CLAUDE.md rule 1) lets <c>Ago.Chat.Infrastructure.Email</c> reference
/// <c>Ago.Chat.Application</c>, never the other way around - but one of this shell's three real callers,
/// <see cref="Ago.Chat.Application.UseCases.CreateOperatorInvite.OperatorInviteCodeMailTemplate"/>, lives
/// inside <c>Ago.Chat.Application</c> itself. Placing the shell in <c>Infrastructure.Email</c> would make
/// that template depend outward on Infrastructure, which the dependency rule forbids outright. This class
/// touches no external resource at all (no <c>DbContext</c>/<c>HttpClient</c>/<c>IConnection</c>/
/// <c>DateTime.Now</c>/<c>Guid.NewGuid</c> - CLAUDE.md rule 2's own list), so it needs no port/adapter
/// split in the first place: it is a pure string-formatting function, the same category
/// <c>Ago.Chat.Application.Mapping</c>'s own mappers already are, just for HTML markup instead of
/// integration-event DTOs. <c>Ago.Chat.Worker</c>'s own two templates
/// (<see cref="Ago.Chat.Worker.InactivityWarningMailTemplate"/>, <see cref="Ago.Chat.Worker.DownloadThresholdWarningMailTemplate"/>)
/// already reach into <c>Ago.Chat.Application.Abstractions</c> for <c>INotificationMailSender</c> today,
/// so putting the shell one folder over, in <c>Ago.Chat.Application.Emailing</c>, costs that project no
/// new reference.</para>
/// </summary>
public static class EmailHtmlShell
{
    public static string Render(EmailShellContent content)
    {
        var preheader = content.Preheader is { Length: > 0 } explicitPreheader ? explicitPreheader : content.Heading;

        var html = new StringBuilder();
        html.Append(Head);
        html.Append(Preheader(preheader));
        html.Append(BodyOpenAndLogoRow);
        html.Append(Hero(content.HeroIcon, content.Heading));
        html.Append(BodyCopy(content.BodyParagraphs));

        if (content.CallToAction is { } cta)
        {
            html.Append(CtaButton(cta));
        }

        if (content.SecondaryNote is { Length: > 0 } note)
        {
            html.Append(Divider);
            html.Append(SecondaryNoteBlock(note));
        }

        html.Append(CardCloseAndFooter);
        return html.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    // The <head> block, verbatim from the mockup - fonts, MSO conditionals, the mobile/dark-scheme media
    // queries. A plain (non-interpolated) string, so every literal '{'/'}' in the CSS below needs no
    // escaping.
    private const string Head = @"<!DOCTYPE html PUBLIC ""-//W3C//DTD XHTML 1.0 Transitional//EN"" ""http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd"">
<html xmlns=""http://www.w3.org/1999/xhtml"" xmlns:v=""urn:schemas-microsoft-com:vml"" xmlns:o=""urn:schemas-microsoft-com:office:office"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<meta http-equiv=""X-UA-Compatible"" content=""IE=edge"">
<meta name=""color-scheme"" content=""light"">
<meta name=""supported-color-schemes"" content=""light"">
<title>AGO Chat</title>
<!--[if mso]>
<noscript>
<xml>
<o:OfficeDocumentSettings>
<o:PixelsPerInch>96</o:PixelsPerInch>
</o:OfficeDocumentSettings>
</xml>
</noscript>
<style>
  table, td { border-collapse: collapse; }
  .mso-font { font-family: Arial, sans-serif !important; }
</style>
<![endif]-->
<link rel=""preconnect"" href=""https://fonts.googleapis.com"">
<link href=""https://fonts.googleapis.com/css2?family=Onest:wght@700;800&family=IBM+Plex+Sans:wght@400;500;600&display=swap"" rel=""stylesheet"" type=""text/css"">
<style type=""text/css"">
  body, table, td, a { -webkit-text-size-adjust: 100%; -ms-text-size-adjust: 100%; }
  table, td { mso-table-lspace: 0pt; mso-table-rspace: 0pt; }
  img { -ms-interpolation-mode: bicubic; border: 0; height: auto; line-height: 100%; outline: none; text-decoration: none; }
  body { margin: 0; padding: 0; width: 100% !important; height: 100% !important; background-color: #eef1f8; }
  a { text-decoration: none; }

  .preheader { display: none !important; visibility: hidden; opacity: 0; color: transparent; height: 0; width: 0; overflow: hidden; mso-hide: all; }

  @media screen and (max-width: 600px) {
    .email-wrap { width: 100% !important; }
    .fluid { width: 100% !important; max-width: 100% !important; }
    .px-24 { padding-left: 24px !important; padding-right: 24px !important; }
    .py-32 { padding-top: 32px !important; padding-bottom: 32px !important; }
    .h1 { font-size: 22px !important; line-height: 29px !important; }
    .hide-mobile { display: none !important; }
  }

  @media (prefers-color-scheme: dark) {
    .dark-ignore { background-color: #eef1f8 !important; color: #0f1728 !important; }
  }
</style>
</head>
";

    private static string Preheader(string text) =>
        $"<body style=\"margin:0; padding:0; background-color:#eef1f8; font-family: 'IBM Plex Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif;\">\r\n\r\n" +
        $"<div class=\"preheader\">{Encode(text)}</div>\r\n\r\n";

    private const string BodyOpenAndLogoRow = @"<center class=""dark-ignore"" style=""width:100%; background-color:#eef1f8;"">

<!--[if mso]>
<table role=""presentation"" width=""600"" align=""center"" cellpadding=""0"" cellspacing=""0"" border=""0""><tr><td>
<![endif]-->

<div class=""email-wrap"" style=""max-width:600px; margin:0 auto;"">

  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
    <tr><td style=""height:40px; line-height:40px; font-size:0;"">&nbsp;</td></tr>
  </table>

  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
    <tr>
      <td align=""center"" style=""padding-bottom:28px;"">
        <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" border=""0"">
          <tr>
            <td valign=""middle"" bgcolor=""#3D5FE0"" style=""width:32px; height:32px; border-radius:9px; background:#3D5FE0; background-image:linear-gradient(140deg,#2F6CE0,#7C4DFF); text-align:center;"">
              <span class=""mso-font"" style=""font-family:'Onest', Arial, sans-serif; font-weight:800; font-size:16px; line-height:32px; color:#ffffff;"">A</span>
            </td>
            <td valign=""middle"" style=""padding-left:10px;"">
              <span class=""mso-font"" style=""font-family:'Onest', Arial, sans-serif; font-weight:800; font-size:18px; color:#0f1728;"">AGO&nbsp;Chat</span>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>

  <table role=""presentation"" class=""fluid"" width=""600"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background-color:#ffffff; border-radius:20px; overflow:hidden; box-shadow:0 1px 2px rgba(15,23,40,0.04);"">
";

    private static string Hero(string heroIcon, string heading) => $@"    <tr>
      <td class=""px-24"" style=""padding:44px 48px 8px 48px;"" bgcolor=""#ffffff"">
        <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
          <tr>
            <td align=""center"" style=""padding-bottom:20px;"">
              <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" border=""0"">
                <tr>
                  <td width=""64"" height=""64"" align=""center"" valign=""middle"" bgcolor=""#EEF2FE"" style=""width:64px; height:64px; border-radius:18px; background-color:#EEF2FE;"">
                    <span style=""font-family:Arial, sans-serif; font-size:28px; line-height:64px;"">{Encode(heroIcon)}</span>
                  </td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td align=""center"">
              <h1 class=""h1 mso-font"" style=""margin:0; font-family:'Onest', Arial, sans-serif; font-weight:800; font-size:26px; line-height:33px; color:#0f1728;"">
                {Encode(heading)}
              </h1>
            </td>
          </tr>
        </table>
      </td>
    </tr>
";

    private static string BodyCopy(IReadOnlyList<string> paragraphs)
    {
        if (paragraphs.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("    <tr>\r\n      <td class=\"px-24\" style=\"padding:8px 48px 4px 48px;\" bgcolor=\"#ffffff\">\r\n");
        foreach (var paragraph in paragraphs)
        {
            sb.Append("        <p style=\"margin:0 0 16px 0; font-family:'IBM Plex Sans', Arial, sans-serif; font-size:15px; line-height:24px; color:#1c2536;\">\r\n");
            sb.Append("          ").Append(Encode(paragraph)).Append("\r\n");
            sb.Append("        </p>\r\n");
        }

        sb.Append("      </td>\r\n    </tr>\r\n");
        return sb.ToString();
    }

    // Bulletproof Outlook button - the mockup's own VML `roundrect` conditional plus a plain anchor for
    // every other client, wrapped in `<!--[if !mso]><!-->...<!--<![endif]-->` so Outlook never renders
    // both.
    private static string CtaButton(EmailShellCallToAction cta)
    {
        var label = Encode(cta.Label);
        var url = Encode(cta.Url);
        return $@"    <tr>
      <td align=""center"" style=""padding:20px 48px 8px 48px;"" bgcolor=""#ffffff"">
        <!--[if mso]>
        <v:roundrect xmlns:v=""urn:schemas-microsoft-com:vml"" xmlns:w=""urn:schemas-microsoft-com:office:word"" href=""{url}"" style=""height:52px;v-text-anchor:middle;width:280px;"" arcsize=""19%"" stroke=""f"" fillcolor=""#2F6CE0"">
        <w:anchorlock/>
        <center style=""color:#ffffff;font-family:Arial,sans-serif;font-size:16px;font-weight:bold;"">{label}</center>
        </v:roundrect>
        <![endif]-->
        <!--[if !mso]><!-->
        <a href=""{url}"" target=""_blank"" style=""display:inline-block; background-color:#2F6CE0; color:#ffffff; font-family:'IBM Plex Sans', Arial, sans-serif; font-size:16px; font-weight:600; line-height:24px; padding:14px 40px; border-radius:10px; text-decoration:none;"">
          {label}
        </a>
        <!--<![endif]-->
      </td>
    </tr>
";
    }

    private const string Divider = @"    <tr>
      <td style=""padding:0 48px;"" bgcolor=""#ffffff"">
        <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
          <tr><td style=""border-top:1px solid #eef0f4; font-size:0; line-height:0;"">&nbsp;</td></tr>
        </table>
      </td>
    </tr>
";

    private static string SecondaryNoteBlock(string note) => $@"    <tr>
      <td class=""px-24"" style=""padding:28px 48px 44px 48px;"" bgcolor=""#ffffff"">
        <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
          <tr>
            <td valign=""top"" width=""40"" style=""padding-right:14px;"">
              <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" border=""0"">
                <tr>
                  <td width=""36"" height=""36"" align=""center"" valign=""middle"" bgcolor=""#F1F8F4"" style=""width:36px; height:36px; border-radius:11px; background-color:#F1F8F4;"">
                    <span style=""font-family:Arial, sans-serif; font-size:16px; line-height:36px; color:#12885C;"">&#10003;</span>
                  </td>
                </tr>
              </table>
            </td>
            <td valign=""middle"">
              <p style=""margin:0; font-family:'IBM Plex Sans', Arial, sans-serif; font-size:14px; line-height:21px; color:#5b6472;"">
                {Encode(note)}
              </p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
";

    // The main card's own closing tag, plus the fixed, brand-only footer - bilingual (this shell's own
    // callers are all bilingual mails, InactivityWarningMailTemplate's own remarks on why), and
    // deliberately carrying no live domain or link: CLAUDE.md's "never write... a real endpoint" applies
    // to this shell exactly as it does to any other file in this repository, and none of this shell's
    // three real callers has a marketing home-page URL in its own configuration to hand it truthfully.
    //
    // The disclaimer line does not claim replies are read - NotificationMailSender's own remarks say its
    // fixed `notifications@{domain}` From address "is never replied to inside this system", the identical
    // no-reply situation Keycloak's own emails are in (confirmed live in `25-73`, `from: no-reply@...`).
    // Lane B's own Keycloak `emailTheme` footer was corrected to the same wording for the same reason
    // while this lane was still in flight - both lanes' shells now carry it identically, so it is copied
    // here rather than reworded a second time.
    private const string CardCloseAndFooter = @"  </table>

  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
    <tr><td style=""height:32px; line-height:32px; font-size:0;"">&nbsp;</td></tr>
  </table>

  <table role=""presentation"" class=""fluid"" width=""600"" cellpadding=""0"" cellspacing=""0"" border=""0"">
    <tr>
      <td align=""center"" class=""px-24"" style=""padding:0 48px;"">
        <p style=""margin:0 0 6px 0; font-family:'IBM Plex Sans', Arial, sans-serif; font-size:13px; line-height:20px; color:#9aa3ba;"">
          AGO&nbsp;Chat &mdash; чат для сайта и мессенджеров / a chat for your website and messengers
        </p>
        <p style=""margin:0; font-family:'IBM Plex Sans', Arial, sans-serif; font-size:12px; line-height:19px; color:#b3bac9;"">
          Это автоматическое сообщение &mdash; на этот адрес нельзя ответить. / This is an automated message &mdash; this address cannot receive replies.
        </p>
      </td>
    </tr>
  </table>

  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
    <tr><td style=""height:40px; line-height:40px; font-size:0;"">&nbsp;</td></tr>
  </table>

</div>

<!--[if mso]>
</td></tr></table>
<![endif]-->

</center>
</body>
</html>
";
}
