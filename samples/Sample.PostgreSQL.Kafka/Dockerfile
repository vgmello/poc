FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Outbox.Core/Outbox.Core.csproj src/Outbox.Core/
COPY src/Outbox.PostgreSQL/Outbox.PostgreSQL.csproj src/Outbox.PostgreSQL/
COPY src/Outbox.Kafka/Outbox.Kafka.csproj src/Outbox.Kafka/
COPY samples/Shared/Shared.csproj samples/Shared/
COPY samples/Sample.PostgreSQL.Kafka/Sample.PostgreSQL.Kafka.csproj samples/Sample.PostgreSQL.Kafka/
RUN dotnet restore samples/Sample.PostgreSQL.Kafka/Sample.PostgreSQL.Kafka.csproj
COPY src/ src/
COPY samples/Shared/ samples/Shared/
COPY samples/Sample.PostgreSQL.Kafka/ samples/Sample.PostgreSQL.Kafka/
RUN dotnet publish samples/Sample.PostgreSQL.Kafka/Sample.PostgreSQL.Kafka.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sample.PostgreSQL.Kafka.dll"]
