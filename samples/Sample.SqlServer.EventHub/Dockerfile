FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Outbox.Core/Outbox.Core.csproj src/Outbox.Core/
COPY src/Outbox.SqlServer/Outbox.SqlServer.csproj src/Outbox.SqlServer/
COPY src/Outbox.EventHub/Outbox.EventHub.csproj src/Outbox.EventHub/
COPY samples/Shared/Shared.csproj samples/Shared/
COPY samples/Sample.SqlServer.EventHub/Sample.SqlServer.EventHub.csproj samples/Sample.SqlServer.EventHub/
RUN dotnet restore samples/Sample.SqlServer.EventHub/Sample.SqlServer.EventHub.csproj
COPY src/ src/
COPY samples/Shared/ samples/Shared/
COPY samples/Sample.SqlServer.EventHub/ samples/Sample.SqlServer.EventHub/
RUN dotnet publish samples/Sample.SqlServer.EventHub/Sample.SqlServer.EventHub.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Sample.SqlServer.EventHub.dll"]
