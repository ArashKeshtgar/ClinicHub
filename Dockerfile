# One image: the React app is built and served by the API from wwwroot.
#
#   docker build -t clinichub .
#
# ClinicHub needs a SQL Server it reaches with a SQL login (a Linux container
# can't use Windows authentication) that may create databases: it creates
# the catalog and one database per clinic itself, at start-up and when an
# administrator opens a new clinic. See README.md, "Run it in Docker".

FROM node:20-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ClinicHub.Api/ClinicHub.Api.csproj src/ClinicHub.Api/
RUN dotnet restore src/ClinicHub.Api/ClinicHub.Api.csproj
COPY src/ src/
RUN dotnet publish src/ClinicHub.Api/ClinicHub.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /web/dist ./wwwroot
RUN mkdir -p /data/keys /data/home && chown -R app:app /data
# /data keeps what must outlive the container: the data-protection keys (they
# encrypt two-factor secrets and clinic connection strings) and, under HOME,
# the certificate the sign-in server signs its tokens with when no
# Sso:SigningCertificatePath is given.
ENV ASPNETCORE_URLS=http://+:8080 \
    DataProtection__KeysPath=/data/keys \
    HOME=/data/home
VOLUME /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClinicHub.Api.dll"]
