using System.Net;

namespace Ago.Chat.Infrastructure.Email;

/// <summary>
/// `25-156`: the HTML half of an operator reply's <c>multipart/alternative</c> body -
/// <see cref="EmailChannelAdapter"/>'s only caller, wrapping the reply in a minimal shell that names the
/// **tenant** whose shop the visitor emailed, not AGO's own brand.
///
/// <para><b>Why this is a new, smaller type rather than a second call into
/// <see cref="Ago.Chat.Application.Emailing.EmailHtmlShell"/>.</b> That shell was read in full before
/// writing this one (`25-155`'s own <c>EmailHtmlShellTests</c> and the mockup it ports pin its exact
/// shape). Two of its fixed, non-parametrized pieces are wrong for this reply, not merely unwanted
/// decoration: its logo row hard-codes the literal text "AGO&nbsp;Chat" and a gradient "A" tile as
/// **this deployment's own** brand identity - the one thing `25-156`'s own backlog item exists to
/// replace with the tenant's own name - and its footer states "this address cannot receive replies",
/// which is true of `NotificationMailSender`'s fixed <c>notifications@{domain}</c> sender but false of
/// this channel's own <c>support+{siteId}@{domain}</c> sender: a visitor's reply to this exact message
/// is expected and threaded (<see cref="EmailChannelAdapter"/>'s own <c>In-Reply-To</c>/<c>References</c>
/// headers), so repeating that disclaimer here would tell the visitor something false. Calling
/// <see cref="Ago.Chat.Application.Emailing.EmailHtmlShell.Render"/> and then trying to strip or override
/// either fixed piece is not an option that method's own parameters (<c>EmailShellContent</c>) expose -
/// both are compiled into its private constants. A smaller sibling is what is left once the two
/// unusable pieces are subtracted from an otherwise-reusable shape, so this type ports what *does* still
/// fit by inspection instead: the same page background (<c>#eef1f8</c>), the same white, rounded,
/// shadowed card (<c>border-radius:20px</c>, <c>box-shadow:0 1px 2px rgba(15,23,40,0.04)</c>) at the same
/// <c>max-width:600px</c>, and the same two-font pairing (<c>'Onest'</c> for the short identity line,
/// <c>'IBM Plex Sans'</c> for body copy) - so a visitor who has already seen `25-155`'s own account mail
/// once reads this as a sibling design, not an unrelated one, even though no code is shared between
/// them.</para>
///
/// <para><b>Why this lives in <c>Ago.Chat.Infrastructure.Email</c>, not
/// <c>Ago.Chat.Application.Emailing</c> next to <see cref="Ago.Chat.Application.Emailing.EmailHtmlShell"/>.</b>
/// That shell had to sit in <c>Application</c> because one of its three real callers
/// (<c>OperatorInviteCodeMailTemplate</c>) already lives there, and the dependency rule forbids
/// <c>Application</c> reaching outward into <c>Infrastructure.Email</c> (that type's own remarks). This
/// type has exactly one real caller, <see cref="EmailChannelAdapter"/>, which already lives in this same
/// project - there is no second caller anywhere in <c>Application</c> to force it upward, so it stays
/// where its only reader is, the same "no premature generalisation" reasoning
/// `docs/architecture/clean-architecture.md`'s qualifying rules ask for before a type is placed
/// somewhere a real caller has not yet asked it to be.</para>
///
/// <para><b>Public, not <c>internal</c>, unlike this project's own <c>EmailMimeMessageBuilder</c>.</b>
/// That class is deliberately <c>internal</c> and tested only through the real SMTP boundary
/// (<c>EmailSmtpClientTests</c>' own remarks: "the same way every other MIME behaviour in this file is
/// proven"), because it is wire-protocol detail - a bug in its base64/boundary handling is only real if
/// it survives a real relay round trip. This type is pure content rendering with no wire-format concern
/// of its own (the identical category <see cref="Ago.Chat.Application.Emailing.EmailHtmlShell"/> is in,
/// and that one is <c>public</c> and unit-tested directly, no send involved) - so it is public here for
/// the same reason, letting a test assert on the exact rendered markup without decoding a base64 MIME
/// part first.</para>
///
/// <para><paramref name="tenantName"/> and the reply body are plain text, HTML-encoded here - never raw
/// HTML a caller hands in, the identical reason <c>EmailShellContent</c>'s own remarks give: both trace
/// back to tenant/operator-supplied data. <paramref name="accentColorHex"/> is not re-validated here - it
/// arrives already validated by <see cref="Ago.Chat.Domain.WidgetConfig"/>'s own constructor (a real
/// <c>#RRGGBB</c> or <see langword="null"/>), the same "validate once, at the value object that owns the
/// value" split every other caller of an already-validated <c>WidgetConfig</c> field already
/// relies on.</para>
/// </summary>
public static class TenantReplyEmailShell
{
    /// <summary>What renders when a site has never set <see cref="Ago.Chat.Domain.WidgetConfig.PrimaryColorHex"/>
    /// - a muted slate, not this deployment's own brand blue (`EmailHtmlShell`'s <c>#2F6CE0</c>): an
    /// unbranded tenant should read as "no colour chosen yet", not as if AGO's own colour had been
    /// applied on the tenant's behalf. The same muted tone `EmailHtmlShell`'s own footer text already
    /// uses (<c>#9aa3ba</c>), reused here rather than inventing a second "neutral grey".</summary>
    public const string NeutralAccentColorHex = "#9AA3BA";

    /// <summary>
    /// `25-160`: <paramref name="logoContentId"/> - non-null exactly when <c>EmailChannelAdapter</c>
    /// resolved a <c>Ready</c> logo, in which case it renders a small <c>&lt;img
    /// src="cid:{logoContentId}"&gt;</c> beside the tenant name instead of the bare text label this
    /// shell rendered before this item. <see langword="null"/> renders exactly the `25-156` markup this
    /// method produced before this item shipped - byte-for-byte, so every existing
    /// <c>TenantReplyEmailShellTests</c> case with no logo keeps passing unchanged.
    /// </summary>
    public static string Render(string tenantName, string replyBody, string? accentColorHex, string? logoContentId = null)
    {
        var accent = accentColorHex is { Length: > 0 } ? accentColorHex : NeutralAccentColorHex;
        var nameRow = logoContentId is { Length: > 0 }
            ? $@"<img src=""cid:{Encode(logoContentId)}"" alt=""{Encode(tenantName)}"" width=""28"" height=""28"" style=""display:inline-block; vertical-align:middle; border-radius:6px; margin-right:8px;"" /><span style=""font-family:'Onest', Arial, sans-serif; font-weight:800; font-size:15px; color:#0f1728; vertical-align:middle;"">{Encode(tenantName)}</span>"
            : $@"<span style=""font-family:'Onest', Arial, sans-serif; font-weight:800; font-size:15px; color:#0f1728;"">{Encode(tenantName)}</span>";

        return $@"<!DOCTYPE html PUBLIC ""-//W3C//DTD XHTML 1.0 Transitional//EN"" ""http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd"">
<html xmlns=""http://www.w3.org/1999/xhtml"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<meta name=""color-scheme"" content=""light"">
<meta name=""supported-color-schemes"" content=""light"">
<title>{Encode(tenantName)}</title>
<link rel=""preconnect"" href=""https://fonts.googleapis.com"">
<link href=""https://fonts.googleapis.com/css2?family=Onest:wght@700;800&family=IBM+Plex+Sans:wght@400;500;600&display=swap"" rel=""stylesheet"" type=""text/css"">
<style type=""text/css"">
  body, table, td, a {{ -webkit-text-size-adjust: 100%; -ms-text-size-adjust: 100%; }}
  table, td {{ mso-table-lspace: 0pt; mso-table-rspace: 0pt; }}
  body {{ margin: 0; padding: 0; width: 100% !important; background-color: #eef1f8; }}
  a {{ text-decoration: none; }}
  @media screen and (max-width: 600px) {{
    .fluid {{ width: 100% !important; max-width: 100% !important; }}
    .px-24 {{ padding-left: 24px !important; padding-right: 24px !important; }}
  }}
</style>
</head>
<body style=""margin:0; padding:0; background-color:#eef1f8; font-family: 'IBM Plex Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif;"">
<center style=""width:100%; background-color:#eef1f8;"">
<div class=""email-wrap"" style=""max-width:600px; margin:0 auto;"">

  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
    <tr><td style=""height:40px; line-height:40px; font-size:0;"">&nbsp;</td></tr>
  </table>

  <table role=""presentation"" class=""fluid"" width=""600"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background-color:#ffffff; border-radius:20px; overflow:hidden; box-shadow:0 1px 2px rgba(15,23,40,0.04);"">
    <tr>
      <td style=""height:4px; line-height:4px; font-size:0;"" bgcolor=""{accent}"">&nbsp;</td>
    </tr>
    <tr>
      <td class=""px-24"" style=""padding:28px 40px 6px 40px;"" bgcolor=""#ffffff"">
        {nameRow}
      </td>
    </tr>
    <tr>
      <td class=""px-24"" style=""padding:8px 40px 36px 40px;"" bgcolor=""#ffffff"">
        <p style=""margin:0; font-family:'IBM Plex Sans', Arial, sans-serif; font-size:15px; line-height:24px; color:#1c2536; white-space:pre-wrap; word-wrap:break-word;"">{Encode(replyBody)}</p>
      </td>
    </tr>
  </table>

  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
    <tr><td style=""height:40px; line-height:40px; font-size:0;"">&nbsp;</td></tr>
  </table>

</div>
</center>
</body>
</html>
";
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
