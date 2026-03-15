FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Outbox.Core/Outbox.Core.csproj src/Outbox.Core/
COPY src/Outbox.PostgreSQL/Outbox.PostgreSQL.csproj src/Outbox.PostgreSQL/
COPY src/Outbox.EventHub/Outbox.EventHub.csproj src/Outbox.EventHub/
COPY samples/Shared/Shared.csproj samples/Shared/
COPY samples/Sample.PostgreSQL.EventHub/Sample.PostgreSQL.EventHub.csproj samples/Sample.PostgreSQL.EventHub/
RUN dotnet restore samples/Sample.PostgreSQL.EventHub/Sample.PostgreSQL.EventHub.csproj
COPY src/ src/
COPY samples/Shared/ samples/Shared/
COPY samples/Sample.PostgreSQL.EventHub/ samples/Sample.PostgreSQL.EventHub/
RUN dotnet publish samples/Sample.PostgreSQL.EventHub/Sample.PostgreSQL.EventHub.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sample.PostgreSQL.EventHub.dll"]
