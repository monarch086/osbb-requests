FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

COPY global.json Directory.Build.props NuGet.Config ./
COPY src/OsbbRequests.DataImport/OsbbRequests.DataImport.csproj src/OsbbRequests.DataImport/packages.lock.json ./src/OsbbRequests.DataImport/
COPY src/OsbbRequests.Shared/OsbbRequests.Shared.csproj src/OsbbRequests.Shared/packages.lock.json ./src/OsbbRequests.Shared/
COPY src/OsbbRequests.Domain/OsbbRequests.Domain.csproj src/OsbbRequests.Domain/packages.lock.json ./src/OsbbRequests.Domain/
COPY src/OsbbRequests.Postgres/OsbbRequests.Postgres.csproj src/OsbbRequests.Postgres/packages.lock.json ./src/OsbbRequests.Postgres/
COPY src/OsbbRequests.Services/OsbbRequests.Services.csproj src/OsbbRequests.Services/packages.lock.json ./src/OsbbRequests.Services/
RUN dotnet restore src/OsbbRequests.DataImport/OsbbRequests.DataImport.csproj --locked-mode

COPY src/OsbbRequests.DataImport/ ./src/OsbbRequests.DataImport/
COPY src/OsbbRequests.Shared/ ./src/OsbbRequests.Shared/
COPY src/OsbbRequests.Domain/ ./src/OsbbRequests.Domain/
COPY src/OsbbRequests.Postgres/ ./src/OsbbRequests.Postgres/
COPY src/OsbbRequests.Services/ ./src/OsbbRequests.Services/
RUN dotnet publish src/OsbbRequests.DataImport/OsbbRequests.DataImport.csproj --configuration Release --no-restore --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/ ./
USER app
ENTRYPOINT ["dotnet", "OsbbRequests.DataImport.dll"]
