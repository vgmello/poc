FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Shared/Shared.csproj Shared/
COPY OutboxPublisher/OutboxPublisher.csproj OutboxPublisher/
RUN dotnet restore OutboxPublisher/OutboxPublisher.csproj
COPY Shared/ Shared/
COPY OutboxPublisher/ OutboxPublisher/
RUN dotnet publish OutboxPublisher/OutboxPublisher.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OutboxPublisher.dll"]
