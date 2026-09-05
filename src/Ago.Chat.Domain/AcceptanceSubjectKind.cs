namespace Ago.Chat.Domain;

/// <summary>
/// `24-01`: the three shapes of person who can accept a document, named because `adr/0076` (`16-04`)
/// already treats them as three different relationships to AGO, not one. <see cref="Tenant"/> and
/// <see cref="Operator"/> are AGO's own account holders - AGO is their controller. <see cref="Visitor"/>
/// is the tenant's own customer - AGO is a processor acting on the tenant's instruction for that data.
/// An <see cref="AcceptanceRecord"/> must be able to name all three without pretending they are the
/// same kind of subject, which is this stage's own wording (`docs/roadmap.md` Stage 24 brief).
///
/// <para><b>Why an enum plus a bare <see cref="Guid"/>, not three nullable strongly-typed id columns.</b>
/// A record has exactly one subject, never zero and never more than one - three nullable columns would
/// let a row exist with none set, or (worse) two set disagreeing with each other, an illegal state this
/// type's own factory methods (<see cref="AcceptanceRecord.ForTenant"/>/<c>ForOperator</c>/<c>ForVisitor</c>)
/// exist precisely to make unrepresentable at the one place a record is ever constructed. The
/// alternative most likely to be proposed later - a discriminated union type wrapping
/// <see cref="SiteId"/>/<see cref="OperatorId"/>/<see cref="VisitorId"/> - was rejected only because
/// C# has no first-class sum type and modelling one by hand here would be a second novel pattern this
/// codebase does not otherwise use, for a benefit (compile-time exhaustiveness) the three factory
/// methods already deliver at the boundary that matters: a caller cannot construct a
/// <see cref="Tenant"/> record with an <see cref="OperatorId"/>'s value, because the factory method
/// only accepts a <see cref="SiteId"/>.</para>
///
/// <para>Stored as `text` (the enum member name), the same
/// <c>HasConversion&lt;string&gt;()</c> shape `MessageAuthorKind`/`ChannelKind` already use, so a
/// future fourth kind is additive by construction, without a migration to widen a `check` constraint.</para>
/// </summary>
public enum AcceptanceSubjectKind
{
    /// <summary>The tenant itself - represented in this codebase by <see cref="SiteId"/>, since a
    /// "site" *is* the shop that registered with AGO (`docs/architecture/personal-data.md`'s own
    /// `sites.name` row: "the customer's business identity"). Named <c>Tenant</c> here rather than
    /// <c>Site</c> to match the language `adr/0076`, the roadmap and the backlog item itself use for
    /// this relationship - the underlying id type does not need to match the subject-kind's name for
    /// the two to refer to the same row.</summary>
    Tenant,

    /// <summary>An operator - an AGO account holder whose own basis for processing is `24-04`'s open
    /// question, not this item's to answer. This item only has to be able to name them.</summary>
    Operator,

    /// <summary>A visitor - the tenant's own customer. AGO carries this acceptance on the tenant's
    /// instruction (`adr/0076`); it does not make AGO a controller of it.</summary>
    Visitor,
}
