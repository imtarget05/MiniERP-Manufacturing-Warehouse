# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    src/Dockerfile
# PURPOSE: Multi-stage Docker build for ASP.NET Core 8 Web API
# ============================================================================

# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY MiniERP.Api.csproj .
RUN dotnet restore MiniERP.Api.csproj

# Copy remaining source code and publish
COPY . .
RUN dotnet publish MiniERP.Api.csproj -c Release -o /app/publish --no-restore

# Stage 2: Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 5000

COPY --from=build /app/publish .

# Run as non-root user for enterprise container security
USER app

ENTRYPOINT ["dotnet", "MiniERP.Api.dll"]
