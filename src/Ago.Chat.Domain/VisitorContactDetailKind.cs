namespace Ago.Chat.Domain;

/// <summary>
/// `14-14`: what kind of fact a <see cref="VisitorContactDetail"/> records - a small, closed set, the
/// same "closed vocabulary, string-converted" shape <see cref="ChannelKind"/> already establishes for
/// itself. Deliberately <b>not</b> reused from <see cref="ChannelKind"/>: that enum names a channel
/// this system can actually route through (`ChannelIdentity`'s own remarks - "one built-in identity
/// mechanism, plus N external ones that link *into* it"), and conflating the two vocabularies would
/// make it look, at a glance, as if recording <see cref="Phone"/> here were one step away from an
/// adapter someday reading it. It never is - see <see cref="VisitorContactDetail"/>'s own remarks.
///
/// <para>Stored as the CLR member name via EF's default string conversion, not an ordinal - the same
/// reasoning <see cref="ChannelKind"/>'s own remarks give: an ordinal makes reordering this enum a
/// silent data corruption.</para>
/// </summary>
public enum VisitorContactDetailKind
{
    Phone,
    Email,

    /// <summary>The visitor's own name, as typed into the widget's own contact-capture form's
    /// dedicated name field (`25-62`) - the one real writer this member has ever had. No confirm/invalid
    /// channel the way <see cref="Phone"/>/<see cref="Email"/> have (<see cref="VisitorContactDetail.SetAssessment"/>'s
    /// own guard) - a name has nothing to dial or deliver to that could come back confirmed or
    /// bounced.</summary>
    Name,
}
