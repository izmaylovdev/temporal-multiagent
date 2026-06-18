# App image — builds and runs both the ASP.NET Core API and the Temporal worker.
# Includes the Docker CLI so the worker can launch sandbox child containers via the
# mounted Docker socket (docker-out-of-docker).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first (better layer caching)
COPY AgentSandbox.sln ./
COPY src/AgentSandbox.Core/AgentSandbox.Core.csproj src/AgentSandbox.Core/
COPY src/AgentSandbox.Api/AgentSandbox.Api.csproj src/AgentSandbox.Api/
COPY src/AgentSandbox.Worker/AgentSandbox.Worker.csproj src/AgentSandbox.Worker/
RUN dotnet restore src/AgentSandbox.Api/AgentSandbox.Api.csproj \
    && dotnet restore src/AgentSandbox.Worker/AgentSandbox.Worker.csproj

COPY src/ src/
RUN dotnet publish src/AgentSandbox.Api/AgentSandbox.Api.csproj    -c Release -o /app/api    --no-restore \
    && dotnet publish src/AgentSandbox.Worker/AgentSandbox.Worker.csproj -c Release -o /app/worker --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends docker.io ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/api    ./api
COPY --from=build /app/worker ./worker

ENV ASPNETCORE_URLS=http://0.0.0.0:8000
# Overridden per-service in docker-compose (worker uses the worker dll).
CMD ["dotnet", "/app/api/AgentSandbox.Api.dll"]
