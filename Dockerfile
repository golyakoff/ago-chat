# syntax=docker/dockerfile:1
#
# One Dockerfile for all three hosts (Api, Worker, Webhooks) - they share the same dependency
# closure (Module -> Application, Infrastructure.Postgres -> Domain), so three near-identical
# files would only be able to drift apart, not stay honestly in sync. Select the host with
# --build-arg PROJECT_NAME=Ago.Chat.Api (see runbooks/local-dev.md for the exact commands).
#
# The local NuGet feed (runbooks/workspace.md) lives outside this repository, so it cannot be
# COPY'd from the normal build context - it is mounted in via Buildx's --build-context instead
# (`docker build --build-context nugetfeed=../.nuget-feed ...`).

ARG PROJECT_NAME
# `15-06`: the commit this image is built from. It is not decoration - it is baked into the compiled
# binary (below) and read back by GET /healthz/version, so a running pod can name its own source.
# Defaults to "unknown" rather than failing the build: a local `docker build` for a quick check is a
# legitimate thing to do, and it should say "unknown" out loud rather than lie or refuse.
ARG GIT_COMMIT=unknown

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT_NAME
ARG GIT_COMMIT
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props nuget.docker.config ./
COPY src/Ago.Chat.Api/Ago.Chat.Api.csproj src/Ago.Chat.Api/
COPY src/Ago.Chat.Worker/Ago.Chat.Worker.csproj src/Ago.Chat.Worker/
COPY src/Ago.Chat.Webhooks/Ago.Chat.Webhooks.csproj src/Ago.Chat.Webhooks/
COPY src/Ago.Chat.Module/Ago.Chat.Module.csproj src/Ago.Chat.Module/
COPY src/Ago.Chat.Application/Ago.Chat.Application.csproj src/Ago.Chat.Application/
COPY src/Ago.Chat.Infrastructure.Postgres/Ago.Chat.Infrastructure.Postgres.csproj src/Ago.Chat.Infrastructure.Postgres/
COPY src/Ago.Chat.Domain/Ago.Chat.Domain.csproj src/Ago.Chat.Domain/

RUN --mount=type=bind,from=nugetfeed,target=/nuget-feed \
    dotnet restore "src/${PROJECT_NAME}/${PROJECT_NAME}.csproj" -r linux-x64 --configfile nuget.docker.config

COPY src/ src/
# -r linux-x64 --self-contained false: still framework-dependent (the base images below carry the
# runtime), but RID-restricted - the build stage is always this SDK image, always linux, so there is
# never a reason to publish for any other RID. Without this, a RID-agnostic publish ships every RID's
# native assets for every native-asset NuGet package in the dependency closure (SkiaSharp, referenced by
# Ago.Chat.Worker for attachment thumbnails - 5-04) under /app/runtimes - ~440MB of win-x64/win-arm64/
# osx/linux-arm64/linux-musl-*/etc. binaries this container can never load. See docs/backlog/8-04-
# container-publish-rid-trim.md.
# -p:SourceRevisionId=<sha>: the SDK appends "+<sha>" to AssemblyInformationalVersion, so the commit
# travels inside the assembly rather than beside it. That is the point (15-06) - an image tag can be
# re-pushed and a Deployment env var can be edited, but the compiled binary cannot disagree with
# itself. BuildInfoResponse reads it back out.
RUN --mount=type=bind,from=nugetfeed,target=/nuget-feed \
    dotnet publish "src/${PROJECT_NAME}/${PROJECT_NAME}.csproj" -c Release -o /app \
      -r linux-x64 --self-contained false --configfile nuget.docker.config \
      -p:SourceRevisionId="${GIT_COMMIT}"

# Bake the concrete DLL name into a fixed filename here, while the build stage still has a shell
# (the SDK image does) - the final stage below is Chiseled, which ships with no shell at all, so
# its ENTRYPOINT must be a literal exec-form array with no runtime `$VAR` expansion. `dotnet <dll>`
# resolves its host config from same-named companions next to the dll (.deps.json/.runtimeconfig.json),
# not just the dll itself - renaming only the dll leaves `dotnet` unable to find `app.deps.json`/
# `app.runtimeconfig.json` and it falls back to (and fails) the self-contained-app code path.
RUN cp "/app/${PROJECT_NAME}.dll" /app/app.dll \
 && cp "/app/${PROJECT_NAME}.deps.json" /app/app.deps.json \
 && cp "/app/${PROJECT_NAME}.runtimeconfig.json" /app/app.runtimeconfig.json

# Ubuntu Chiseled: current .NET guidance's default recommendation for production with no special
# requirements - smaller than Alpine in practice, no shell/package manager (smallest attack
# surface), glibc-based so it sidesteps Alpine's musl-compatibility risk for native dependencies
# (Npgsql, StackExchange.Redis, RabbitMQ.Client). See docs/backlog/8-00-minimal-production-base-
# image.md for the fuller reasoning and the verification this switch was checked against.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
ARG PROJECT_NAME
ARG GIT_COMMIT
# `15-06`: the OCI annotations a registry and `docker inspect`/`crane config` read. `.source` is not
# only documentation - GHCR uses it to link the published package back to this repository, which is
# what makes the package inherit the repository's own visibility instead of arriving orphaned.
# `.revision` is the same commit baked into the binary above, repeated where tooling that cannot run
# the container can still see it.
LABEL org.opencontainers.image.source="https://github.com/golyakoff/ago-chat" \
      org.opencontainers.image.description="AGO Chat host: ${PROJECT_NAME}" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.revision="${GIT_COMMIT}"
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "app.dll"]
