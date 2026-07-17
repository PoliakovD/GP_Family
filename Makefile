COMPOSE = docker compose
.DEFAULT_GOAL = help

.PHONY: help dev dev-restart dev-npm dev-rebuild logs logs-web logs-api

help:
	@echo ""
	@echo "  make dev            Запустить все сервисы (первый раз или после down)"
	@echo "  make dev-restart    Перезапустить web без удаления volumes (быстро)"
	@echo "  make dev-npm        Обновить npm-пакеты в контейнере (после package.json)"
	@echo "  make dev-rebuild    Полный сброс: всё удалить + пересобрать с нуля"
	@echo "  make logs           Логи всех сервисов"
	@echo "  make logs-web       Логи web"
	@echo "  make logs-api       Логи api"
	@echo ""

## Запустить все сервисы (без пересборки образов)
dev:
	$(COMPOSE) up -d

## Перезапустить web-контейнер без удаления npm/Angular volumes (быстро, ≈5 сек)
dev-restart:
	$(COMPOSE) restart web

## Пересобрать образ web с нуля при изменении package.json (удаляет только web-volumes)
dev-npm:
	$(COMPOSE) rm -sf web
	docker volume rm $$(docker volume ls -q | grep web-node-modules) 2>/dev/null || true
	docker volume rm $$(docker volume ls -q | grep web-angular-cache) 2>/dev/null || true
	$(COMPOSE) up --build -d web

## Полный сброс окружения: удалить ВСЕ контейнеры и volumes (БД тоже), пересобрать
dev-rebuild:
	$(COMPOSE) down -v
	$(COMPOSE) up --build -d

logs:
	$(COMPOSE) logs -f

logs-web:
	$(COMPOSE) logs -f web

logs-api:
	$(COMPOSE) logs -f api
