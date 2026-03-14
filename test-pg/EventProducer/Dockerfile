FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Shared/Shared.csproj Shared/
COPY EventProducer/EventProducer.csproj EventProducer/
RUN dotnet restore EventProducer/EventProducer.csproj
COPY Shared/ Shared/
COPY EventProducer/ EventProducer/
RUN dotnet publish EventProducer/EventProducer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "EventProducer.dll"]
