FROM node:26.0.0-bookworm-slim AS frontend
WORKDIR /build/web
COPY web/package*.json ./
RUN npm ci --ignore-scripts
COPY web/ ./
RUN npm run build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
ARG SOURCE_REVISION=unversioned
COPY Directory.Build.props ./
COPY src/Fitness.Core/ src/Fitness.Core/
COPY src/Fitness.Api/ src/Fitness.Api/
COPY --from=frontend /build/web/dist/ src/Fitness.Api/wwwroot/
RUN dotnet publish src/Fitness.Api/Fitness.Api.csproj -c Release -o /app -p:RestoreLockedMode=true -p:SourceRevisionId=${SOURCE_REVISION}
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/ ./
ENV PORT=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Fitness.Api.dll"]
