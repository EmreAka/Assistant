FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Assistant.Api/Assistant.Api.csproj", "Assistant.Api/"]
RUN dotnet restore "Assistant.Api/Assistant.Api.csproj"
COPY . .
WORKDIR "/src/Assistant.Api"
RUN dotnet build "./Assistant.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Assistant.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip git docker.io \
    && python3 -m pip install --no-cache-dir --break-system-packages git+https://github.com/philschmid/code-sandbox-mcp.git \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=publish /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Assistant.Api.dll"]
