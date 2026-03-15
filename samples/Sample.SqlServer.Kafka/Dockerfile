FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Outbox.Core/Outbox.Core.csproj src/Outbox.Core/
COPY src/Outbox.SqlServer/Outbox.SqlServer.csproj src/Outbox.SqlServer/
COPY src/Outbox.Kafka/Outbox.Kafka.csproj src/Outbox.Kafka/
COPY samples/Shared/Shared.csproj samples/Shared/
COPY samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj samples/Sample.SqlServer.Kafka/
RUN dotnet restore samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj
COPY src/ src/
COPY samples/Shared/ samples/Shared/
COPY samples/Sample.SqlServer.Kafka/ samples/Sample.SqlServer.Kafka/
RUN dotnet publish samples/Sample.SqlServer.Kafka/Sample.SqlServer.Kafka.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sample.SqlServer.Kafka.dll"]
