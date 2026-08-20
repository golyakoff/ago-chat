# AGO Chat

The first product on AGO Platform: an embeddable customer-support chat. A shop drops one script tag
on its site, visitors chat from a widget, operators answer from a console.

This repository holds the product's Domain, Application, Contracts, Infrastructure and Module, plus
the three deployables built from them: `Ago.Chat.Api` (connections, commands, queries),
`Ago.Chat.Worker` (consumers, outbox dispatch, assignment) and `Ago.Chat.Webhooks` (outbound
delivery to tenant endpoints, isolated because a third party's latency is not ours to fix).

It consumes `Ago.Platform.*` as NuGet packages, restored from the local feed
(`../ago-root/docs/runbooks/workspace.md`). It never reaches into the platform's source - except
through the dev override below, which must never survive to a merged branch.

## Rules

- Layering and what goes where: `../ago-root/docs/architecture/clean-architecture.md`
- Why three deployables: `../ago-root/docs/adr/0013-*`
- Decisions: `../ago-root/docs/adr/`
- Working agreements: `../ago-root/CLAUDE.md`
- Full project layout: `../ago-root/docs/conventions/naming-and-structure.md`

## Dev override

For a change that genuinely spans this repository and `ago-platform`, set `AgoPlatformDevOverride`
to build against a sibling `../ago-platform` checkout instead of the published package:

```bash
AgoPlatformDevOverride=true dotnet build
```

**A branch that gets merged must build against the published package.** CI never sets this
variable, so a branch left in override mode fails in CI even if it built locally - that failure is
the check catching exactly the API break the package boundary exists to catch.
