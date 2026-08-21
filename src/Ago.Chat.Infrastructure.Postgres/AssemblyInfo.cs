using System.Runtime.CompilerServices;

// RoleRecord/OperatorRoleRecord and AgoChatDbContext.Roles/OperatorRoles stay internal to everything
// else (Domain, Application, Module never see the RBAC storage shape) - the integration tests need
// to seed and inspect them directly, since nothing above IPermissionChecker exposes a way to manage
// roles yet (1-04, adr/0016).
[assembly: InternalsVisibleTo("Ago.Chat.Integration.Tests")]
