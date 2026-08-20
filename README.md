# AGO Chat

The first product on AGO Platform: an embeddable customer-support chat. A shop drops one script tag
on its site, visitors chat from a widget, operators answer from a console.

This repository holds the product's Domain, Application, Contracts, Infrastructure and Module, plus
the three deployables built from them: `Ago.Chat.Api` (connections, commands, queries),
`Ago.Chat.Worker` (consumers, outbox dispatch, assignment) and `Ago.Chat.Webhooks` (outbound
delivery to tenant endpoints, isolated because a third party's latency is not ours to fix).

It consumes `Ago.Platform.*` as NuGet packages. It never reaches into the platform's source.

## Rules

- Layering and what goes where: `../ago-root/docs/architecture/clean-architecture.md`
- Why three deployables: `../ago-root/docs/adr/0013-*`
- Decisions: `../ago-root/docs/adr/`
- Working agreements: `../ago-root/CLAUDE.md`

The structure arrives with the work; the intended shape is in
`../ago-root/docs/conventions/naming-and-structure.md`.
