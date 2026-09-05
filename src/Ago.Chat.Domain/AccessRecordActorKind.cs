namespace Ago.Chat.Domain;

/// <summary>
/// `24-12`: which vocabulary <c>access_records.actor_id</c> is drawn from - the same
/// discriminator-plus-bare-id shape `adr/0076`'s <c>AcceptanceSubjectKind</c> already established for
/// <c>acceptance_records.subject_id</c>, reused here for the same reason: the two actors this table
/// ever names have no shared identifier space, so a bare column needs a tag to say what it holds
/// rather than three nullable typed columns for two cases (`AcceptanceSubjectKind`'s own remarks make
/// the identical argument for three subjects).
///
/// <para><see cref="Operator"/> rows carry that operator's <see cref="OperatorId"/>, stringified -
/// an ordinary row in `operators`, reachable by a site's own report. <see cref="PlatformOwner"/> rows
/// carry the caller's Keycloak `sub` claim, read at the HTTP edge
/// (`Ago.Chat.Api`'s owner endpoints, not `Ago.Chat.Application`) - `adr/0032` gives the platform
/// owner no `operators` row at all, so there is no domain identifier to name them by other than the
/// one Keycloak itself signs. This is exactly the layering `ListSitesForOwnerHandler`'s own remarks
/// already draw for the *authorization* question ("claims are a transport concern... `Ago.Chat.Application`
/// has no port that can see one") - applied here to the *audit* question instead. Recording who acted
/// is not a second, weaker copy of the permission check `RequirePlatformOwner` already made (nothing
/// here gates anything), so writing the record where the claim naturally lives does not reopen that
/// argument; it is a different question read from the same place.</para>
/// </summary>
public enum AccessRecordActorKind
{
    Operator,
    PlatformOwner,
}
