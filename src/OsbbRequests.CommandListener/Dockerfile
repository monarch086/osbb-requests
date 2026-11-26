FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

COPY global.json Directory.Build.props NuGet.Config ./
COPY src/OsbbRequests.CommandListener/OsbbRequests.CommandListener.csproj src/OsbbRequests.CommandListener/packages.lock.json ./src/OsbbRequests.CommandListener/
COPY src/OsbbRequests.CommandProcessor/OsbbRequests.CommandProcessor.csproj src/OsbbRequests.CommandProcessor/packages.lock.json ./src/OsbbRequests.CommandProcessor/
COPY src/OsbbRequests.Aws/OsbbRequests.Aws.csproj src/OsbbRequests.Aws/packages.lock.json ./src/OsbbRequests.Aws/
COPY src/OsbbRequests.Shared/OsbbRequests.Shared.csproj src/OsbbRequests.Shared/packages.lock.json ./src/OsbbRequests.Shared/
COPY src/OsbbRequests.Services/OsbbRequests.Services.csproj src/OsbbRequests.Services/packages.lock.json ./src/OsbbRequests.Services/
COPY src/OsbbRequests.Domain/OsbbRequests.Domain.csproj src/OsbbRequests.Domain/packages.lock.json ./src/OsbbRequests.Domain/
RUN dotnet restore src/OsbbRequests.CommandListener/OsbbRequests.CommandListener.csproj --locked-mode

COPY src/OsbbRequests.CommandListener/ ./src/OsbbRequests.CommandListener/
COPY src/OsbbRequests.CommandProcessor/ ./src/OsbbRequests.CommandProcessor/
COPY src/OsbbRequests.Aws/ ./src/OsbbRequests.Aws/
COPY src/OsbbRequests.Shared/ ./src/OsbbRequests.Shared/
COPY src/OsbbRequests.Services/ ./src/OsbbRequests.Services/
COPY src/OsbbRequests.Domain/ ./src/OsbbRequests.Domain/
RUN dotnet publish src/OsbbRequests.CommandListener/OsbbRequests.CommandListener.csproj --configuration Release --no-restore --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/ ./
USER app
ENTRYPOINT ["dotnet", "OsbbRequests.CommandListener.dll"]
