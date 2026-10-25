FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Shop.Api/Shop.Api.csproj Shop.Api/
RUN dotnet restore Shop.Api/Shop.Api.csproj
COPY Shop.Api/ Shop.Api/
RUN dotnet publish Shop.Api/Shop.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data \
    && chown app:app /data
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Shop="Data Source=/data/shop.db;Default Timeout=30"
USER app
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=3s --start-period=30s --retries=12 \
    CMD curl --fail --silent http://localhost:8080/api/health > /dev/null || exit 1
ENTRYPOINT ["dotnet", "Shop.Api.dll"]
