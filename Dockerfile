FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY Expenses.Api/Expenses.Api.csproj Expenses.Api/
COPY Expenses.Application/Expenses.Application.csproj Expenses.Application/
COPY Expenses.Domain/Expenses.Domain.csproj Expenses.Domain/
COPY Expenses.Infrastructure/Expenses.Infrastructure.csproj Expenses.Infrastructure/
RUN dotnet restore Expenses.Api/Expenses.Api.csproj

COPY . .
RUN dotnet publish Expenses.Api/Expenses.Api.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=10s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error --max-time 8 http://127.0.0.1:8080/health/db > /dev/null || exit 1

ENTRYPOINT ["dotnet", "Expenses.Api.dll"]
