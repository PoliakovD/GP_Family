# Продовый образ Telegram-бота — зеркало src/FamilyHub.Api/Dockerfile (стадии сборки/рантайма),
# без Angular-стадии (у бота нет SPA) и без krb5-libs (был нужен только Npgsql/Kerberos —
# у бота нет ни Npgsql, ни доступа к БД вообще, см. план выноса бота / ADR-0008).
#
# Собирать из корня репозитория (контекст "."), не из src/FamilyHub.TelegramBot:
#   docker build -f src/FamilyHub.TelegramBot/Dockerfile -t familyhub-telegrambot .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY src/FamilyHub.TelegramBot/FamilyHub.TelegramBot.csproj  FamilyHub.TelegramBot/
COPY src/FamilyHub.Contracts/FamilyHub.Contracts.csproj      FamilyHub.Contracts/

RUN dotnet restore FamilyHub.TelegramBot/FamilyHub.TelegramBot.csproj

COPY src/ .

RUN dotnet publish FamilyHub.TelegramBot/FamilyHub.TelegramBot.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

WORKDIR /app
COPY --from=build /app/publish .

# $APP_UID — непривилегированный пользователь (см. комментарий в src/FamilyHub.Api/Dockerfile).
USER $APP_UID

# 8080 — та же конвенция, что у api: порт наружу не публикуется вообще, Caddy обращается к
# wg-client:8080 (бот делит сетевой namespace с WG-сайдкаром, см. deploy/docker-compose.prod.yml).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FamilyHub.TelegramBot.dll"]
