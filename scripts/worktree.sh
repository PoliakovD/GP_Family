#!/usr/bin/env bash
# Управление git worktree для параллельной разработки FamilyHub с изолированными
# docker-compose стеками. Паритет scripts/worktree.ps1 (Bash/macOS/Linux) — см. комментарий
# там же для объяснения, почему нужно переписывать COMPOSE_PROJECT_NAME и порты.
#
# Использование:
#   scripts/worktree.sh new feature/medications-search [slot]
#   scripts/worktree.sh list
#   scripts/worktree.sh rm feature/medications-search
set -euo pipefail

PORT_VARS=(POSTGRES_PORT MINIO_API_PORT MINIO_CONSOLE_PORT SEQ_PORT KAFKA_HOST_PORT API_PORT WEB_PORT)

repo_root() {
    git rev-parse --show-toplevel
}

worktrees_root() {
    local repo; repo=$(repo_root)
    echo "$(dirname "$repo")/worktrees"
}

to_slug() {
    echo "$1" | tr -c 'a-zA-Z0-9' '-' | sed -e 's/^-*//' -e 's/-*$//' | tr 'A-Z' 'a-z'
}

next_slot() {
    local root="$1"
    if [ ! -d "$root" ]; then echo 1; return; fi
    local count; count=$(find "$root" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')
    echo $((count + 1))
}

cmd_new() {
    local branch="${1:?Использование: worktree.sh new <branch> [slot]}"
    local slot="${2:-0}"
    local repo; repo=$(repo_root)
    local root; root=$(worktrees_root)
    local slug; slug=$(to_slug "$branch")
    [ -n "$slug" ] || { echo "Не удалось получить имя каталога из ветки '$branch'." >&2; exit 1; }

    if [ "$slot" -le 0 ]; then slot=$(next_slot "$root"); fi

    local target="$root/$slug"
    [ ! -e "$target" ] || { echo "Каталог уже существует: $target" >&2; exit 1; }
    mkdir -p "$root"

    echo "Создаю worktree '$branch' -> $target (slot $slot)..."
    if git -C "$repo" show-ref --verify --quiet "refs/heads/$branch"; then
        git -C "$repo" worktree add "$target" "$branch"
    else
        git -C "$repo" worktree add "$target" -b "$branch"
    fi

    local source_env="$repo/.env"
    [ -f "$source_env" ] || { echo ".env не найден в $repo — сначала настройте .env в основном worktree (см. .env.example)." >&2; exit 1; }

    local offset=$((slot * 100))
    local project_name="familyhub-$slug"
    local target_env="$target/.env"
    local saw_project_name=0

    : > "$target_env"
    while IFS= read -r line || [ -n "$line" ]; do
        if [[ "$line" =~ ^COMPOSE_PROJECT_NAME= ]]; then
            echo "COMPOSE_PROJECT_NAME=$project_name" >> "$target_env"
            saw_project_name=1
            continue
        fi

        local handled=0
        for var in "${PORT_VARS[@]}"; do
            if [[ "$line" =~ ^${var}=([0-9]+)[[:space:]]*$ ]]; then
                local new_port=$(( ${BASH_REMATCH[1]} + offset ))
                echo "${var}=${new_port}" >> "$target_env"
                echo "  $var -> $new_port"
                handled=1
                break
            fi
        done
        [ "$handled" -eq 1 ] || echo "$line" >> "$target_env"
    done < "$source_env"

    if [ "$saw_project_name" -eq 0 ]; then
        { echo "COMPOSE_PROJECT_NAME=$project_name"; echo; cat "$target_env"; } > "$target_env.tmp"
        mv "$target_env.tmp" "$target_env"
    fi

    local claude_settings="$repo/.claude/settings.local.json"
    if [ -f "$claude_settings" ]; then
        mkdir -p "$target/.claude"
        cp "$claude_settings" "$target/.claude/settings.local.json"
    fi

    echo
    echo "Готово: $target"
    echo "COMPOSE_PROJECT_NAME=$project_name"
    echo "cd \"$target\" && docker compose up -d"
}

cmd_list() {
    local repo; repo=$(repo_root)
    echo "=== git worktree list ==="
    git -C "$repo" worktree list
    echo
    echo "=== docker compose stacks (запущенные) ==="
    docker compose ls
}

cmd_rm() {
    local branch="${1:?Использование: worktree.sh rm <branch>}"
    local repo; repo=$(repo_root)
    local root; root=$(worktrees_root)
    local slug; slug=$(to_slug "$branch")
    local target="$root/$slug"
    local project_name="familyhub-$slug"

    if [ -d "$target" ]; then
        echo "Останавливаю docker-стек '$project_name' (с volume'ами)..."
        (cd "$target" && docker compose -p "$project_name" down -v) || true
    else
        echo "Каталог $target не найден — пробую снять стек '$project_name' вслепую." >&2
        docker compose -p "$project_name" down -v 2>/dev/null || true
    fi

    echo "Удаляю worktree $target..."
    git -C "$repo" worktree remove "$target" --force
    echo "Готово."
}

case "${1:-}" in
    new)  shift; cmd_new "$@" ;;
    list) shift; cmd_list "$@" ;;
    rm)   shift; cmd_rm "$@" ;;
    *)
        echo "Использование: $0 {new|list|rm} ..." >&2
        exit 1
        ;;
esac
