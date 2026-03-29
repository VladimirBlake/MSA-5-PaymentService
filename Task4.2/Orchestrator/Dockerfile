# ─── Build stage ───
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore
COPY Orchestrator/Orchestrator.csproj Orchestrator/
RUN dotnet restore Orchestrator/Orchestrator.csproj

# Copy all source files (including bpmn and form in parent dir)
COPY . .

# Publish
WORKDIR /src/Orchestrator
RUN dotnet publish -c Release -o /app/publish --no-restore

# ─── Runtime stage ───
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Orchestrator.dll"]
