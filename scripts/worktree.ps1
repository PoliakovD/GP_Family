#Requires -Version 7
<#
.SYNOPSIS
    Управление git worktree для параллельной разработки FamilyHub с изолированными
    docker-compose стеками.

.DESCRIPTION
    docker compose по умолчанию берёт имя проекта из имени каталога — контейнеры, сеть и,
    что важнее всего, named volumes (postgres-data, minio-data, ...) окажутся ОДИНАКОВЫМИ у
    двух worktree с одинаковым .env, и второй запущенный стек начнёт работать поверх данных
    первого. Этот скрипт при создании worktree переписывает в его копии .env: свой
    COMPOSE_PROJECT_NAME и хостовые порты со смещением slot*100 — стеки становятся полностью
    независимыми (см. деплой-план, часть A).

.EXAMPLE
    ./scripts/worktree.ps1 new feature/medications-search
    ./scripts/worktree.ps1 new feature/x 2
    ./scripts/worktree.ps1 list
    ./scripts/worktree.ps1 rm feature/medications-search
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('new', 'list', 'rm')]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$Branch,

    [Parameter(Position = 2)]
    [int]$Slot = 0
)

$ErrorActionPreference = 'Stop'

# Переменные, чьи значения — номера портов на хосте (см. .env.example/docker-compose.yml).
# COMPOSE_PROJECT_NAME обрабатывается отдельно, не через эту таблицу.
$PortVars = @('POSTGRES_PORT', 'MINIO_API_PORT', 'MINIO_CONSOLE_PORT', 'SEQ_PORT', 'KAFKA_HOST_PORT', 'API_PORT', 'WEB_PORT')

function Get-RepoRoot {
    $root = git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0) { throw "Не git-репозиторий (запускать из основного worktree GP_Family)." }
    return ($root -replace '/', '\')
}

function Get-WorktreesRoot([string]$repoRoot) {
    # Рядом с репозиторием, не внутри — иначе .dockerignore/.gitignore пришлось бы городить
    # исключения, а IDE путалась бы в двух .git в одном дереве.
    Join-Path (Split-Path $repoRoot -Parent) 'worktrees'
}

function ConvertTo-Slug([string]$branch) {
    ($branch -replace '[^a-zA-Z0-9]+', '-').Trim('-').ToLowerInvariant()
}

function Get-NextSlot([string]$worktreesRoot) {
    if (-not (Test-Path $worktreesRoot)) { return 1 }
    $existing = Get-ChildItem $worktreesRoot -Directory -ErrorAction SilentlyContinue
    return $existing.Count + 1
}

$repoRoot = Get-RepoRoot
$worktreesRoot = Get-WorktreesRoot $repoRoot

switch ($Command) {
    'new' {
        if (-not $Branch) { throw "Использование: worktree.ps1 new <branch> [slot]" }
        if ($Slot -le 0) { $Slot = Get-NextSlot $worktreesRoot }

        $slug = ConvertTo-Slug $Branch
        if (-not $slug) { throw "Не удалось получить имя каталога из ветки '$Branch'." }
        $target = Join-Path $worktreesRoot $slug
        if (Test-Path $target) { throw "Каталог уже существует: $target" }

        New-Item -ItemType Directory -Force -Path $worktreesRoot | Out-Null

        Write-Host "Создаю worktree '$Branch' -> $target (slot $Slot)..."
        git -C $repoRoot show-ref --verify --quiet "refs/heads/$Branch"
        if ($LASTEXITCODE -eq 0) {
            git -C $repoRoot worktree add $target $Branch
        } else {
            git -C $repoRoot worktree add $target -b $Branch
        }

        $sourceEnv = Join-Path $repoRoot '.env'
        if (-not (Test-Path $sourceEnv)) {
            throw ".env не найден в $repoRoot — сначала настройте .env в основном worktree (см. .env.example)."
        }

        $offset = $Slot * 100
        $composeProjectName = "familyhub-$slug"
        $portMap = [ordered]@{}
        $sawProjectName = $false

        $newLines = foreach ($line in (Get-Content $sourceEnv)) {
            $handled = $false

            if ($line -match '^COMPOSE_PROJECT_NAME=') {
                $sawProjectName = $true
                "COMPOSE_PROJECT_NAME=$composeProjectName"
                $handled = $true
            } else {
                foreach ($var in $PortVars) {
                    if ($line -match "^$var=(\d+)\s*$") {
                        $newPort = [int]$Matches[1] + $offset
                        $portMap[$var] = $newPort
                        "$var=$newPort"
                        $handled = $true
                        break
                    }
                }
            }

            if (-not $handled) { $line }
        }

        if (-not $sawProjectName) {
            $newLines = @("COMPOSE_PROJECT_NAME=$composeProjectName", '') + $newLines
        }

        Set-Content -Path (Join-Path $target '.env') -Value $newLines -Encoding utf8

        # Гитигнорен целиком (.gitignore), но нужен агенту/IDE в каждом worktree.
        $claudeSettingsSource = Join-Path $repoRoot '.claude\settings.local.json'
        if (Test-Path $claudeSettingsSource) {
            New-Item -ItemType Directory -Force -Path (Join-Path $target '.claude') | Out-Null
            Copy-Item $claudeSettingsSource (Join-Path $target '.claude\settings.local.json') -Force
        }

        Write-Host ''
        Write-Host "Готово: $target"
        Write-Host "COMPOSE_PROJECT_NAME=$composeProjectName"
        Write-Host "Порты (slot $Slot, смещение +$offset):"
        foreach ($var in $PortVars) {
            if ($portMap.Contains($var)) {
                Write-Host ("  {0,-20} {1}" -f $var, $portMap[$var])
            }
        }
        Write-Host ''
        Write-Host "cd `"$target`""
        Write-Host 'docker compose up -d'
    }

    'list' {
        Write-Host '=== git worktree list ==='
        git -C $repoRoot worktree list
        Write-Host ''
        Write-Host '=== docker compose stacks (запущенные) ==='
        docker compose ls
    }

    'rm' {
        if (-not $Branch) { throw "Использование: worktree.ps1 rm <branch>" }
        $slug = ConvertTo-Slug $Branch
        $target = Join-Path $worktreesRoot $slug
        $composeProjectName = "familyhub-$slug"

        if (Test-Path $target) {
            Write-Host "Останавливаю docker-стек '$composeProjectName' (с volume'ами)..."
            Push-Location $target
            try { docker compose -p $composeProjectName down -v } finally { Pop-Location }
        } else {
            Write-Warning "Каталог $target не найден — пробую снять стек '$composeProjectName' вслепую."
            docker compose -p $composeProjectName down -v 2>$null
        }

        Write-Host "Удаляю worktree $target..."
        git -C $repoRoot worktree remove $target --force
        Write-Host 'Готово.'
    }
}
