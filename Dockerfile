# One Dockerfile for every app in the repo. Pick the project at build time and the DLL at run time:
#   docker build --build-arg PROJECT=src/ApiAggregator.Gateway/ApiAggregator.Gateway.csproj -t aggregator-gateway .
#   docker run aggregator-gateway ApiAggregator.Gateway.dll
# The runtime image is "chiseled": no shell, no package manager, runs as a non-root user.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /repo
COPY . .
RUN dotnet publish "$PROJECT" -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet"]
