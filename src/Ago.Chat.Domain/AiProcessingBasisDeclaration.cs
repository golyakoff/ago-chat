namespace Ago.Chat.Domain;

/// <summary>
/// `25-04` decision 5: <b>the tenant's own declaration that they have a lawful basis for their
/// visitors' conversation text reaching an LLM vendor.</b> Recorded, never verified - Art. 6 ч. 4 puts
/// that obligation on the operator (the tenant), not on the processor (AGO), so the honest thing this
/// system can hold is a dated statement with a name against it.
///
/// <para><b>Why this is not a column on <see cref="AiAddOnEnablement"/>, and not folded into an
/// <see cref="AcceptanceRecord"/> either - the item's own hardest requirement.</b> Accepting our terms
/// and asserting a fact about somebody else are two different statements by two different legal
/// mechanisms: the acceptance settles AGO's relationship with the tenant (a `24-01` record naming the
/// document version they read), while this declaration is the tenant speaking about *their own
/// visitors*, on a basis AGO never sees. A reader asking "on what basis did this conversation reach a
/// vendor" must be able to tell which of the two was made, when, and by whom - and a single row
/// carrying both, or a boolean on the enablement, collapses exactly that distinction. Two tables means
/// a tenant can be in three legible states rather than two: accepted-but-not-declared,
/// declared-but-not-accepted, and both - and <c>EnableAiAddOnHandler</c> refuses the first two with
/// different errors.</para>
///
/// <para><b>Immutable and insert-only, the identical shape <see cref="AcceptanceRecord"/> chose for the
/// identical reason.</b> There is no method on this type that changes anything, and
/// <c>IAiProcessingBasisDeclarationRepository.SaveAsync</c> only ever inserts - a tenant who declares
/// again after re-reading the agreement gets a *second row*, which is what keeps "what did they assert
/// in March" answerable after a later declaration in June. A withdrawal is expressed by disabling the
/// add-on (<see cref="AiAddOnEnablement.Disable"/>), never by deleting or editing this evidence.</para>
///
/// <para><b><see cref="DeclaredBy"/> is an <see cref="OperatorId"/>, not a bare string.</b> Unlike
/// <see cref="ModuleQuantityGrant.UnconditionalGrantSetBy"/> (the platform owner, a cross-tenant
/// identity with no row in any site's roster), the person who may declare this is always one of the
/// tenant's own operators holding <see cref="Permission.SiteConfigure"/> - an identity this codebase
/// does have a row and a type for, so weakening it to a string would lose a join the audit answer
/// actually needs ("who is Ivan, and does he still work here").</para>
/// </summary>
public sealed class AiProcessingBasisDeclaration
{
    // The same two bounds AcceptanceRecord carries, for the same two fields and the same reason.
    public const int MaxClientIpLength = AcceptanceRecord.MaxClientIpLength;
    public const int MaxUserAgentLength = AcceptanceRecord.MaxUserAgentLength;

    public AiProcessingBasisDeclarationId Id { get; }

    public SiteId SiteId { get; }

    public OperatorId DeclaredBy { get; }

    public DateTimeOffset DeclaredAt { get; }

    /// <summary>The request's own client address at the moment of declaring - nullable for the same
    /// reason <see cref="AcceptanceRecord.ClientIp"/> is: a caller that cannot supply one must not be
    /// forced to invent a value.</summary>
    public string? ClientIp { get; }

    public string? UserAgent { get; }

    private AiProcessingBasisDeclaration(
        AiProcessingBasisDeclarationId id, SiteId siteId, OperatorId declaredBy, DateTimeOffset declaredAt,
        string? clientIp, string? userAgent)
    {
        Id = id;
        SiteId = siteId;
        DeclaredBy = declaredBy;
        DeclaredAt = declaredAt;
        ClientIp = clientIp;
        UserAgent = userAgent;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private AiProcessingBasisDeclaration()
    {
    }

    public static AiProcessingBasisDeclaration Declare(
        AiProcessingBasisDeclarationId id, SiteId siteId, OperatorId declaredBy, DateTimeOffset declaredAt,
        string? clientIp = null, string? userAgent = null)
    {
        if (siteId.Value == Guid.Empty)
        {
            throw new ArgumentException("A site id cannot be empty.", nameof(siteId));
        }

        if (declaredBy.Value == Guid.Empty)
        {
            throw new ArgumentException("A declaring operator id cannot be empty.", nameof(declaredBy));
        }

        if (clientIp is { Length: > MaxClientIpLength })
        {
            throw new ArgumentException($"A client IP cannot exceed {MaxClientIpLength} characters.", nameof(clientIp));
        }

        if (userAgent is { Length: > MaxUserAgentLength })
        {
            throw new ArgumentException($"A user agent cannot exceed {MaxUserAgentLength} characters.", nameof(userAgent));
        }

        return new AiProcessingBasisDeclaration(id, siteId, declaredBy, declaredAt, clientIp, userAgent);
    }
}
