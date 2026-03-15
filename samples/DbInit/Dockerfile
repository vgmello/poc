FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY . .
RUN dotnet publish samples/DbInit/DbInit.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

COPY src/Outbox.SqlServer/db_scripts/install.sql /db_scripts/sqlserver/install.sql
COPY src/Outbox.PostgreSQL/db_scripts/install.sql /db_scripts/pgsql/install.sql

ENTRYPOINT ["dotnet", "DbInit.dll"]
