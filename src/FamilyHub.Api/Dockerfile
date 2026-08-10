# Продовый образ (в отличие от Dockerfile.dev, который тоже собирает Release, но НЕ включает
# Angular — тот раздаётся отдельным контейнером с ng serve в локальном docker-compose.yml).
# Собирает SPA и API в одном образе: FamilyHub.Api сам раздаёт wwwroot (UseStaticFiles +
# MapFallbackToFile, см. Program.cs) — на VPS одному контейнеру не нужен сосед для фронтенда.
#
# Собирать из корня репозитория (контекст "."), не из src/FamilyHub.Api — иначе COPY src/...
# ниже не найдёт файлы:
#   docker build -f src/FamilyHub.Api/Dockerfile -t familyhub-api .

# --- Стадия 1: Angular SPA -----------------------------------------------------------------
# Первой, не последней: слой npm ci кэшируется независимо от последующих изменений в C#
# (тот же принцип, что COPY *.csproj + restore до COPY src/ в стадии build ниже).
FROM node:22-alpine AS web
WORKDIR /src/FamilyHub.Web
COPY src/FamilyHub.Web/package.json src/FamilyHub.Web/package-lock.json ./
RUN npm ci
COPY src/FamilyHub.Web/ ./
# angular.json: outputPath.base = "../FamilyHub.Api/wwwroot" (относительно этого WORKDIR,
# т.е. ложится в /src/FamilyHub.Api/wwwroot) — defaultConfiguration уже "production"
# (outputHashing, serviceWorker, бюджеты 850kb/1.5mb), доп. флаг конфигурации не нужен.
RUN npx ng build

# --- Стадия 2: сборка .NET (та же структура, что Dockerfile.dev) --------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY src/FamilyHub.Api/FamilyHub.Api.csproj                              FamilyHub.Api/
COPY src/FamilyHub.Domain/FamilyHub.Domain.csproj                        FamilyHub.Domain/
COPY src/FamilyHub.Contracts/FamilyHub.Contracts.csproj                  FamilyHub.Contracts/
COPY src/FamilyHub.Infrastructure/FamilyHub.Infrastructure.csproj        FamilyHub.Infrastructure/
COPY src/FamilyHub.Modules.Birthdays/FamilyHub.Modules.Birthdays.csproj  FamilyHub.Modules.Birthdays/
COPY src/FamilyHub.Modules.Medical/FamilyHub.Modules.Medical.csproj      FamilyHub.Modules.Medical/

RUN dotnet restore FamilyHub.Api/FamilyHub.Api.csproj

COPY src/ .
# SPA из стадии web — ПОСЛЕ COPY src/ (wwwroot гитигнорен и в контексте сборки, как правило,
# отсутствует вовсе; даже если бы присутствовал устаревший — этот COPY кладёт свежую сборку
# поверх). dotnet publish ниже подхватывает wwwroot как contentFiles автоматически (Web SDK).
COPY --from=web /src/FamilyHub.Api/wwwroot ./FamilyHub.Api/wwwroot

RUN dotnet publish FamilyHub.Api/FamilyHub.Api.csproj -c Release -o /app/publish --no-restore

# --- Стадия 3: рантайм ----------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

RUN apk add --no-cache krb5-libs

WORKDIR /app
COPY --from=build /app/publish .

# $APP_UID — непривилегированный пользователь, который сам определяет базовый образ aspnet
# (.NET 8+): https://github.com/dotnet/dotnet-docker/blob/main/documentation/scenarios/non-root-user.md.
# Dockerfile.dev остаётся под root — там это не имеет значения (только локальный docker-compose).
USER $APP_UID

# 8080, не 5276 (как в Dockerfile.dev/docker-compose.yml): порт наружу не публикуется вообще —
# Caddy обращается к api по имени сервиса внутри docker-сети (см. deploy/docker-compose.prod.yml),
# 8080 — конвенция образов Microsoft для контейнеров без привилегированного порта.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FamilyHub.Api.dll"]
