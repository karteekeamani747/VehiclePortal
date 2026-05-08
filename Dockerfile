# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["VehiclePortal.csproj", "."]
RUN dotnet restore "VehiclePortal.csproj"

COPY . .
RUN dotnet publish "VehiclePortal.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ── Stage 2: Runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN mkdir -p /app/data /app/uploads

COPY --from=build /app/publish .

EXPOSE 8080
EXPOSE 8081

ENTRYPOINT ["dotnet", "VehiclePortal.dll"]