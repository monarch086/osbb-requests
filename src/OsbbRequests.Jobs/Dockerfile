FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

COPY global.json Directory.Build.props NuGet.Config ./
COPY src/OsbbRequests.Jobs/OsbbRequests.Jobs.csproj src/OsbbRequests.Jobs/packages.lock.json ./src/OsbbRequests.Jobs/
COPY src/OsbbRequests.CommandProcessor/OsbbRequests.CommandProcessor.csproj src/OsbbRequests.CommandProcessor/packages.lock.json ./src/OsbbRequests.CommandProcessor/
COPY src/OsbbRequests.Postgres/OsbbRequests.Postgres.csproj src/OsbbRequests.Postgres/packages.lock.json ./src/OsbbRequests.Postgres/
COPY src/OsbbRequests.Services/OsbbRequests.Services.csproj src/OsbbRequests.Services/packages.lock.json ./src/OsbbRequests.Services/
COPY src/OsbbRequests.Domain/OsbbRequests.Domain.csproj src/OsbbRequests.Domain/packages.lock.json ./src/OsbbRequests.Domain/
RUN dotnet restore src/OsbbRequests.Jobs/OsbbRequests.Jobs.csproj --locked-mode

COPY src/OsbbRequests.Jobs/ ./src/OsbbRequests.Jobs/
COPY src/OsbbRequests.CommandProcessor/ ./src/OsbbRequests.CommandProcessor/
COPY src/OsbbRequests.Postgres/ ./src/OsbbRequests.Postgres/
COPY src/OsbbRequests.Services/ ./src/OsbbRequests.Services/
COPY src/OsbbRequests.Domain/ ./src/OsbbRequests.Domain/
RUN dotnet publish src/OsbbRequests.Jobs/OsbbRequests.Jobs.csproj --configuration Release --no-restore --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/ ./
USER app
ENTRYPOINT ["dotnet", "OsbbRequests.Jobs.dll"]
