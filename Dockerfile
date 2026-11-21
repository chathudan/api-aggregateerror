# One Dockerfile for every app in the repo. Pick the project at build time and the DLL at run time:
#   docker build --build-arg PROJECT=src/ApiAggregator.Gateway/ApiAggregator.Gateway.csproj -t aggregator-gateway .
#   docker run aggregator-gateway ApiAggregator.Gateway.dll
# The runtime image is "chiseled": no shell, no package manager, runs as a non-root user.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /repo
COPY . .
# Without PROJECT, dotnet publish would build the whole solution (tests too) into one folder and fail on
# clashing appsettings.json files. Hosts that don't read docker-compose.yml (Fly.io, Render...) must pass it.
RUN if [ -z "$PROJECT" ]; then echo "Set the build argument PROJECT, e.g. --build-arg PROJECT=src/ApiAggregator.Gateway/ApiAggregator.Gateway.csproj" >&2; exit 1; fi
RUN dotnet publish "$PROJECT" -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet"]
