FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Shared/Shared.csproj Shared/
COPY DbInit/DbInit.csproj DbInit/
RUN dotnet restore DbInit/DbInit.csproj
COPY Shared/ Shared/
COPY DbInit/ DbInit/
RUN dotnet publish DbInit/DbInit.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "DbInit.dll"]
