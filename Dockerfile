# syntax=docker/dockerfile:1

# MdReader web version, built by the fly.io remote builder (`fly deploy`).
# Only the cross-platform projects are built: MdReader.slnx also contains the Windows-only WPF app.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src

# global.json pins the Windows machines' SDK band (10.0.400). Accept whichever .NET 10 SDK the image ships.
COPY global.json Directory.Build.props Directory.Packages.props ./
RUN printf '{\n  "sdk": { "version": "10.0.100", "rollForward": "latestFeature" }\n}\n' > global.json

# Restore first: this layer stays cached until a project file or a package version changes.
COPY src/MdReader.Core/MdReader.Core.csproj src/MdReader.Core/
COPY src/MdReader.Web/MdReader.Web.csproj src/MdReader.Web/
RUN dotnet restore src/MdReader.Web/MdReader.Web.csproj

COPY src/MdReader.Core/ src/MdReader.Core/
COPY src/MdReader.Web/ src/MdReader.Web/
COPY web/ web/
COPY webapp/ webapp/
RUN dotnet publish src/MdReader.Web/MdReader.Web.csproj -c Release --no-restore -o /app/publish -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app
COPY --from=build /app/publish .
# The aspnet images ship a non-root "app" user; APP_UID is its numeric id.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MdReader.Web.dll"]
