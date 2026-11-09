# One Dockerfile for both services:
#   docker build --build-arg PROJECT=Shorty.Api       -t shorty-api .
#   docker build --build-arg PROJECT=Shorty.Analytics -t shorty-analytics .
#
# Behind a proxy that inspects TLS, restoring packages inside the build fails. Publish on the host instead and package that:
#   dotnet publish src/Shorty.Api -c Release -o artifacts/api
#   docker build --target prebuilt --build-arg PROJECT=Shorty.Api --build-arg PUBLISH_DIR=artifacts/api -t shorty-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, from the project files alone, so a code change does not download every package again.
COPY global.json nuget.config Directory.Build.props .editorconfig ./
COPY src/Shorty.Core/*.csproj src/Shorty.Core/
COPY src/Shorty.ServiceDefaults/*.csproj src/Shorty.ServiceDefaults/
COPY src/Shorty.Api/*.csproj src/Shorty.Api/
COPY src/Shorty.Analytics/*.csproj src/Shorty.Analytics/
ARG PROJECT
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
ARG PROJECT
ENV PROJECT_DLL=${PROJECT}.dll \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

FROM base AS prebuilt
ARG PUBLISH_DIR
COPY ${PUBLISH_DIR} .
USER app
ENTRYPOINT ["sh", "-c", "exec dotnet ${PROJECT_DLL}"]

# The default target (last stage): what CI and Azure build.
FROM base AS runtime
COPY --from=build /app .
# The aspnet image ships an unprivileged "app" user.
USER app
ENTRYPOINT ["sh", "-c", "exec dotnet ${PROJECT_DLL}"]
