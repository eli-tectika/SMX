FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Smx.Domain/ Smx.Domain/
COPY Smx.SearchProxy.Contracts/ Smx.SearchProxy.Contracts/
COPY Smx.Infrastructure/ Smx.Infrastructure/
COPY Smx.Orchestrator/ Smx.Orchestrator/
RUN dotnet publish Smx.Orchestrator/Smx.Orchestrator.csproj -c Release -o /app

# aspnet, NOT runtime: the orchestrator is still change-feed driven, but since it also serves the
# interview SSE surface it is an Sdk.Web app and its runtimeconfig.json now requires the
# Microsoft.AspNetCore.App framework. On the plain runtime image it fails at STARTUP with "framework
# not found" — and a crash-looping orchestrator silently stops the change feed, which is how this
# system loses stage dispatch without anything reporting an error.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Smx.Orchestrator.dll"]
