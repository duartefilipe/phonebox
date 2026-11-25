# PhoneBox hub: the agent in hub mode, for a home server (Docker, Portainer). See docker/README.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY agent/PhoneBox.Agent/ agent/PhoneBox.Agent/
COPY android/app/src/main/assets/www/ android/app/src/main/assets/www/
RUN dotnet publish agent/PhoneBox.Agent -c Release -f net10.0 -o /out --nologo -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV PHONEBOX_HUB=1 \
    XDG_CONFIG_HOME=/data \
    XDG_DATA_HOME=/data \
    HOME=/data \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /data && chown app:app /data
WORKDIR /app
COPY --from=build /out .
USER app
VOLUME /data
# 8766: the phones (HTTPS, paired key); 8767: the settings page (HTTPS, PHONEBOX_PASSWORD)
EXPOSE 8766 8767
ENTRYPOINT ["dotnet", "PhoneBoxAgent.dll", "--hub"]
