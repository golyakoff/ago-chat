using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListVisitorContactDetails;

/// <summary>`14-14`: the console's own contact-details listing, beside (not merged into) `14-12`'s
/// `ListChannelIdentitiesForVisitor` query - see <see cref="ListVisitorContactDetailsHandler"/>'s own
/// remarks for why this reuses <see cref="Permission.ConversationRead"/> rather than that query's
/// narrower assigned-operator check.</summary>
public sealed record ListVisitorContactDetails(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
