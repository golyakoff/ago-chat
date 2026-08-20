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

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT_NAME
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
    dotnet restore "src/${PROJECT_NAME}/${PROJECT_NAME}.csproj" --configfile nuget.docker.config

COPY src/ src/
RUN --mount=type=bind,from=nugetfeed,target=/nuget-feed \
    dotnet publish "src/${PROJECT_NAME}/${PROJECT_NAME}.csproj" -c Release -o /app \
      --configfile nuget.docker.config

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG PROJECT_NAME
ENV PROJECT_DLL="${PROJECT_NAME}.dll"
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$PROJECT_DLL\""]
