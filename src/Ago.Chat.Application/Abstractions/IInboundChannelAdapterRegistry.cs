using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-01`: resolves the one <see cref="IInboundChannelAdapter"/> that serves a given
/// <see cref="ChannelKind"/>.
///
/// <para><b>Why a port rather than injecting <c>IEnumerable&lt;IInboundChannelAdapter&gt;</c>.</b> The
/// enumerable works, and it is what the implementation actually does - but it makes every caller
/// responsible for the same three decisions (pick by <see cref="IInboundChannelAdapter.Kind"/>, decide
/// what "two adapters claim the same kind" means, decide what "none registered" means), and it ties
/// the call site to a DI-container convention rather than to a stated contract. Declaring the question
/// once is the same reasoning that put <see cref="IConversationRepository.GetActiveForVisitorAsync"/>
/// on a port instead of leaving every handler to write the query.</para>
///
/// <para>Returns <see langword="null"/> rather than throwing for an unregistered channel: a host that
/// deliberately runs without the SMS adapter is a supported configuration, not a bug, and the caller
/// is the only thing that knows whether a missing channel is fatal there.</para>
/// </summary>
public interface IInboundChannelAdapterRegistry
{
    IInboundChannelAdapter? For(ChannelKind kind);
}
