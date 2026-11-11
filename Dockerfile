# .NET API with the media tools required by VideoImports. No deployment is performed here.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/FyxBody.Videos.Api -c Release -o /app/publish

FROM node:22-bookworm-slim AS javascript

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
USER root
ARG YT_DLP_VERSION=2026.10.26
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg python3 python3-venv ca-certificates libatomic1 \
    && python3 -m venv /opt/ytdlp \
    && /opt/ytdlp/bin/pip install --no-cache-dir "yt-dlp[default]==${YT_DLP_VERSION}" \
    && rm -rf /var/lib/apt/lists/*
COPY --from=javascript /usr/local/bin/node /usr/local/bin/node
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    VideoImports__YtDlpPath=/opt/ytdlp/bin/yt-dlp \
    VideoImports__FfmpegPath=/usr/bin/ffmpeg \
    VideoImports__FfprobePath=/usr/bin/ffprobe \
    VideoImports__JavaScriptRuntime=node:/usr/local/bin/node
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FyxBody.Videos.Api.dll"]
