FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Smx.Domain/ Smx.Domain/
COPY Smx.Infrastructure/ Smx.Infrastructure/
COPY Smx.Backend/ Smx.Backend/
RUN dotnet publish Smx.Backend/Smx.Backend.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Smx.Backend.dll"]
