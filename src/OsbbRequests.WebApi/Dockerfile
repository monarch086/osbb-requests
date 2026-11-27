FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

COPY global.json Directory.Build.props NuGet.Config ./
COPY src/OsbbRequests.WebApi/OsbbRequests.WebApi.csproj src/OsbbRequests.WebApi/packages.lock.json ./src/OsbbRequests.WebApi/
COPY src/OsbbRequests.Cognito/OsbbRequests.Cognito.csproj src/OsbbRequests.Cognito/packages.lock.json ./src/OsbbRequests.Cognito/
COPY src/OsbbRequests.Services/OsbbRequests.Services.csproj src/OsbbRequests.Services/packages.lock.json ./src/OsbbRequests.Services/
COPY src/OsbbRequests.Domain/OsbbRequests.Domain.csproj src/OsbbRequests.Domain/packages.lock.json ./src/OsbbRequests.Domain/
COPY src/OsbbRequests.Postgres/OsbbRequests.Postgres.csproj src/OsbbRequests.Postgres/packages.lock.json ./src/OsbbRequests.Postgres/
COPY src/OsbbRequests.CommandProcessor/OsbbRequests.CommandProcessor.csproj src/OsbbRequests.CommandProcessor/packages.lock.json ./src/OsbbRequests.CommandProcessor/
RUN dotnet restore src/OsbbRequests.WebApi/OsbbRequests.WebApi.csproj --locked-mode

COPY src/OsbbRequests.WebApi/ ./src/OsbbRequests.WebApi/
COPY src/OsbbRequests.Cognito/ ./src/OsbbRequests.Cognito/
COPY src/OsbbRequests.Services/ ./src/OsbbRequests.Services/
COPY src/OsbbRequests.Domain/ ./src/OsbbRequests.Domain/
COPY src/OsbbRequests.Postgres/ ./src/OsbbRequests.Postgres/
COPY src/OsbbRequests.CommandProcessor/ ./src/OsbbRequests.CommandProcessor/
RUN dotnet publish src/OsbbRequests.WebApi/OsbbRequests.WebApi.csproj --configuration Release --no-restore --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/ ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "OsbbRequests.WebApi.dll"]
