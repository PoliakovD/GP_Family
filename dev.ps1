param(
    [Parameter(Position = 0)]
    [string]$Command = "help"
)

$ErrorActionPreference = "Stop"

function Invoke-Help {
    Write-Host ""
    Write-Host "  .\dev.ps1 dev            Запустить все сервисы"
    Write-Host "  .\dev.ps1 dev-restart-web    Перезапустить web (быстро, volumes не трогает)"
    Write-Host "  .\dev.ps1 dev-rebuild-api    Пересобрать api"
    Write-Host "  .\dev.ps1 dev-rebuild-api-web    Пересобрать api+web"
    Write-Host "  .\dev.ps1 dev-npm        Пересобрать web после изменения package.json"
    Write-Host "  .\dev.ps1 dev-rebuild    Полный сброс: всё удалить + пересобрать"
    Write-Host "  .\dev.ps1 logs           Логи всех сервисов"
    Write-Host "  .\dev.ps1 logs-web       Логи web"
    Write-Host "  .\dev.ps1 logs-api       Логи api"
    Write-Host ""
}

switch ($Command) {
    "help" {
        Invoke-Help
    }

    "dev" {
        docker compose up -d
    }

    "dev-restart-web" {
        docker compose restart web
    }
    
    "dev-restart-api" {
        docker compose restart api
    }

    "dev-npm" {
        # Пересобрать web-образ с нуля (нужно при изменении package.json)
        docker compose rm -sf web
        $project = (Split-Path -Leaf (Get-Location)).ToLower() -replace '[^a-z0-9]', ''
        "web-node-modules", "web-angular-cache" | ForEach-Object {
            $vol = "${project}_$_"
            $exists = docker volume ls -q | Where-Object { $_ -eq $vol }
            if ($exists) {
                Write-Host "Удаляю volume: $vol"
                docker volume rm $vol
            }
        }
        docker compose up --build -d web
    }

    "dev-rebuild" {
        # Полный сброс: удаляет ВСЕ volumes включая БД
        docker compose down -v
        docker compose up --build -d
    }
    "dev-rebuild-api" {
            # Полный сброс: удаляет ВСЕ volumes включая БД
            docker compose down api -v
            docker compose up api --build -d
        }
    "dev-rebuild-api-web" {
            # Полный сброс: удаляет ВСЕ volumes включая БД
            docker compose down api -v
            docker compose up api --build -d
            # Пересобрать web-образ с нуля (нужно при изменении package.json)
                    docker compose rm -sf web
                    $project = (Split-Path -Leaf (Get-Location)).ToLower() -replace '[^a-z0-9]', ''
                    "web-node-modules", "web-angular-cache" | ForEach-Object {
                        $vol = "${project}_$_"
                        $exists = docker volume ls -q | Where-Object { $_ -eq $vol }
                        if ($exists) {
                            Write-Host "Удаляю volume: $vol"
                            docker volume rm $vol
                        }
                    }
                    docker compose up --build -d web
        }

    "logs" {
        docker compose logs -f
    }

    "logs-web" {
        docker compose logs -f web
    }

    "logs-api" {
        docker compose logs -f api
    }

    default {
        Write-Host "Неизвестная команда: $Command"
        Invoke-Help
        exit 1
    }
}
