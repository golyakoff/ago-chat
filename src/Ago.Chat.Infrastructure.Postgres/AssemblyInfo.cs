using System.Runtime.CompilerServices;

// RoleRecord/OperatorRoleRecord and AgoChatDbContext.Roles/OperatorRoles stay internal to everything
// else (Domain, Application, Module never see the RBAC storage shape) - the integration and
// concurrency tests need to seed and inspect them directly, since nothing above IPermissionChecker
// exposes a way to manage roles yet (1-04, adr/0016).
[assembly: InternalsVisibleTo("Ago.Chat.Integration.Tests")]
[assembly: InternalsVisibleTo("Ago.Chat.Concurrency.Tests")]

// `4-05`: InboundMessage/MessageBatchWriter.FlushAsync stay internal to everything else too -
// Ago.Chat.Module.Pipeline (ConversationSequencer/BatchAccumulator/MessagePipelineWorkerHost/
// BatchFlusherService) is the only outside caller, and it can only be that (never Application or
// Domain) because Module already depends on this project, not the other way round.
[assembly: InternalsVisibleTo("Ago.Chat.Module")]
