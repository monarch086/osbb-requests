FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

COPY global.json Directory.Build.props NuGet.Config ./
COPY src/OsbbRequests.CommandListener/OsbbRequests.CommandListener.csproj src/OsbbRequests.CommandListener/packages.lock.json ./src/OsbbRequests.CommandListener/
COPY src/OsbbRequests.CommandProcessor/OsbbRequests.CommandProcessor.csproj src/OsbbRequests.CommandProcessor/packages.lock.json ./src/OsbbRequests.CommandProcessor/
COPY src/OsbbRequests.Postgres/OsbbRequests.Postgres.csproj src/OsbbRequests.Postgres/packages.lock.json ./src/OsbbRequests.Postgres/
COPY src/OsbbRequests.Services/OsbbRequests.Services.csproj src/OsbbRequests.Services/packages.lock.json ./src/OsbbRequests.Services/
COPY src/OsbbRequests.Domain/OsbbRequests.Domain.csproj src/OsbbRequests.Domain/packages.lock.json ./src/OsbbRequests.Domain/
RUN dotnet restore src/OsbbRequests.CommandListener/OsbbRequests.CommandListener.csproj --locked-mode

COPY src/OsbbRequests.CommandListener/ ./src/OsbbRequests.CommandListener/
COPY src/OsbbRequests.CommandProcessor/ ./src/OsbbRequests.CommandProcessor/
COPY src/OsbbRequests.Postgres/ ./src/OsbbRequests.Postgres/
COPY src/OsbbRequests.Services/ ./src/OsbbRequests.Services/
COPY src/OsbbRequests.Domain/ ./src/OsbbRequests.Domain/
RUN dotnet publish src/OsbbRequests.CommandListener/OsbbRequests.CommandListener.csproj --configuration Release --no-restore --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/ ./
USER app
ENTRYPOINT ["dotnet", "OsbbRequests.CommandListener.dll"]
