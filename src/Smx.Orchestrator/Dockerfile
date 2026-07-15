FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Smx.Domain/ Smx.Domain/
COPY Smx.SearchProxy.Contracts/ Smx.SearchProxy.Contracts/
COPY Smx.Infrastructure/ Smx.Infrastructure/
COPY Smx.Orchestrator/ Smx.Orchestrator/
RUN dotnet publish Smx.Orchestrator/Smx.Orchestrator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS run
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Smx.Orchestrator.dll"]
