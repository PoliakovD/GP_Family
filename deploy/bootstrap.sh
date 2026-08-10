#!/usr/bin/env bash
# Идемпотентный провижининг чистого Ubuntu 24.04 под прод-контур FamilyHub (деплой-план, часть G).
# Запускать ОДИН раз от root на свежем VPS:
#
#   scp deploy/bootstrap.sh root@<host>:/root/
#   ssh root@<host> 'bash /root/bootstrap.sh "ssh-ed25519 AAAA... you@laptop"'
#
# Аргумент — публичный SSH-ключ, который получит доступ как пользователь deploy (тот, под которым
# GitHub Actions потом заходит по SSH — см. .github/workflows/deploy.yml, секрет SSH_PRIVATE_KEY —
# должна быть ПРИВАТНАЯ половина той же пары). Повторный запуск безопасен: каждый шаг проверяет,
# нужно ли что-то делать, прежде чем менять состояние.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "Запускать от root (sudo)." >&2
    exit 1
fi

DEPLOY_PUBKEY="${1:-}"
if [ -z "$DEPLOY_PUBKEY" ]; then
    echo "Использование: $0 \"<публичный SSH-ключ пользователя deploy>\"" >&2
    exit 1
fi

log() { echo "==> $*"; }

# --- 1. Базовые обновления ---------------------------------------------------------------------
log "Обновление пакетов..."
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get -y -qq upgrade
apt-get -y -qq install \
    unattended-upgrades apt-transport-https ca-certificates curl gnupg lsb-release \
    ufw fail2ban wireguard

# --- 2. Docker CE + compose plugin ---------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
    log "Устанавливаю Docker CE..."
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
        > /etc/apt/sources.list.d/docker.list
    apt-get update -qq
    apt-get -y -qq install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
else
    log "Docker уже установлен ($(docker --version)), пропускаю установку."
fi
systemctl enable --now docker >/dev/null

# --- 3. Пользователь deploy + каталоги -----------------------------------------------------------
if ! id deploy >/dev/null 2>&1; then
    log "Создаю пользователя deploy..."
    useradd -m -s /bin/bash -G docker deploy
else
    log "Пользователь deploy уже существует."
    usermod -aG docker deploy
fi

install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
touch /home/deploy/.ssh/authorized_keys
if ! grep -qxF "$DEPLOY_PUBKEY" /home/deploy/.ssh/authorized_keys; then
    echo "$DEPLOY_PUBKEY" >> /home/deploy/.ssh/authorized_keys
fi
chmod 600 /home/deploy/.ssh/authorized_keys
chown -R deploy:deploy /home/deploy/.ssh

install -d -m 750 -o deploy -g deploy /opt/familyhub
install -d -m 700 -o deploy -g deploy /opt/familyhub/backups
install -d -m 750 -o deploy -g deploy /opt/familyhub/caddy-data

# --- 4. SSH hardening ------------------------------------------------------------------------------
log "SSH hardening..."
cat > /etc/ssh/sshd_config.d/99-familyhub-hardening.conf <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
EOF
systemctl reload ssh 2>/dev/null || systemctl reload sshd

# --- 5. ufw -------------------------------------------------------------------------------------------
log "Настраиваю ufw..."
ufw --force reset >/dev/null
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw limit 22/tcp >/dev/null                 # rate-limit, не просто allow — тормозит перебор пароля/ключа
ufw allow 80/tcp >/dev/null                 # Caddy: HTTP-01 + редирект на HTTPS
ufw allow 443/tcp >/dev/null                # Caddy: публичный сайт
ufw allow 443/udp >/dev/null                # Caddy: HTTP/3 (QUIC)
ufw allow 51820/udp >/dev/null              # WireGuard
ufw allow in on wg0 >/dev/null              # весь трафик из WG-туннеля (админки, см. Caddyfile)
# Docker сам управляет своими iptables-цепочками (DOCKER-USER и т.п.) в обход ufw по умолчанию —
# FORWARD-политика ufw должна быть ACCEPT, иначе WireGuard-пиры не достучатся до контейнеров
# (см. деплой-план, критичная деталь маршрутизации).
sed -i 's/^DEFAULT_FORWARD_POLICY=.*/DEFAULT_FORWARD_POLICY="ACCEPT"/' /etc/default/ufw
ufw --force enable >/dev/null

# --- 6. fail2ban ---------------------------------------------------------------------------------------
log "Включаю fail2ban для sshd..."
cat > /etc/fail2ban/jail.d/sshd.local <<'EOF'
[sshd]
enabled = true
maxretry = 5
bantime = 1h
findtime = 10m
EOF
systemctl enable --now fail2ban >/dev/null
systemctl restart fail2ban

# --- 7. sysctl + swap ------------------------------------------------------------------------------------
log "sysctl (Kafka требует vm.max_map_count, форвардинг нужен WireGuard)..."
cat > /etc/sysctl.d/99-familyhub.conf <<'EOF'
net.ipv4.ip_forward=1
net.core.somaxconn=1024
vm.max_map_count=262144
EOF
sysctl --system >/dev/null

if [ ! -f /swapfile ]; then
    log "Создаю 4G swap-файл (страховка на пиках Kafka/Seq/сборки)..."
    fallocate -l 4G /swapfile
    chmod 600 /swapfile
    mkswap /swapfile >/dev/null
    swapon /swapfile
    echo '/swapfile none swap sw 0 0' >> /etc/fstab
else
    log "swap-файл уже существует, пропускаю."
fi

# --- 8. WireGuard (на хосте, не в контейнере) -----------------------------------------------------------
log "Настраиваю WireGuard..."
install -d -m 700 /etc/wireguard

if [ ! -f /etc/wireguard/server_private.key ]; then
    (umask 077 && wg genkey | tee /etc/wireguard/server_private.key | wg pubkey > /etc/wireguard/server_public.key)
fi
SERVER_PRIVATE_KEY=$(cat /etc/wireguard/server_private.key)
SERVER_PUBLIC_KEY=$(cat /etc/wireguard/server_public.key)

if [ ! -f /etc/wireguard/laptop_private.key ]; then
    (umask 077 && wg genkey | tee /etc/wireguard/laptop_private.key | wg pubkey > /etc/wireguard/laptop_public.key)
fi
LAPTOP_PRIVATE_KEY=$(cat /etc/wireguard/laptop_private.key)
LAPTOP_PUBLIC_KEY=$(cat /etc/wireguard/laptop_public.key)

WAN_IFACE=$(ip route show default | awk '/default/ {print $5; exit}')

if [ ! -f /etc/wireguard/wg0.conf ]; then
    cat > /etc/wireguard/wg0.conf <<EOF
[Interface]
Address = 10.8.0.1/24
ListenPort = 51820
PrivateKey = $SERVER_PRIVATE_KEY
PostUp = iptables -A FORWARD -i wg0 -j ACCEPT; iptables -A FORWARD -o wg0 -j ACCEPT; iptables -t nat -A POSTROUTING -o $WAN_IFACE -j MASQUERADE
PostDown = iptables -D FORWARD -i wg0 -j ACCEPT; iptables -D FORWARD -o wg0 -j ACCEPT; iptables -t nat -D POSTROUTING -o $WAN_IFACE -j MASQUERADE

[Peer]
# Ноутбук с LM Studio (см. деплой-план, единственный пир — офлайн-деградация уже реализована
# в LmStudioJsonClient, отсутствие пира не валит контур).
PublicKey = $LAPTOP_PUBLIC_KEY
AllowedIPs = 10.8.0.2/32
EOF
    chmod 600 /etc/wireguard/wg0.conf
else
    log "wg0.conf уже существует — не переписываю (ключи в /etc/wireguard/*.key переиспользованы)."
fi

systemctl enable wg-quick@wg0 >/dev/null
systemctl restart wg-quick@wg0

# --- 9. Резюме -------------------------------------------------------------------------------------------
SERVER_IP=$(curl -s -4 --max-time 3 ifconfig.me || echo "<IP сервера>")

cat <<SUMMARY

============================================================
Готово. Что дальше:

1. В ДРУГОМ терминале (не закрывая текущую root-сессию) проверьте вход под deploy —
   PasswordAuthentication уже выключен, обратной дороги через пароль не будет:
     ssh deploy@${SERVER_IP}

2. GitHub Secrets (Settings -> Secrets and variables -> Actions) для .github/workflows/deploy.yml:
     SSH_HOST          = ${SERVER_IP}
     SSH_USER          = deploy
     SSH_PRIVATE_KEY   = <приватная половина ключа, чей публичный передан аргументом этого скрипта>
     SSH_KNOWN_HOSTS   = вывод на своей машине: ssh-keyscan ${SERVER_IP}
     PROD_ENV          = содержимое .env для прода (см. .env.example, секция "VPS-деплой")

3. Конфиг WireGuard для ноутбука с LM Studio — сохранить как familyhub-laptop.conf и импортировать
   в клиент WireGuard (Windows/macOS/Linux):

-----8<----- familyhub-laptop.conf -----8<-----
[Interface]
PrivateKey = ${LAPTOP_PRIVATE_KEY}
Address = 10.8.0.2/32

[Peer]
PublicKey = ${SERVER_PUBLIC_KEY}
Endpoint = ${SERVER_IP}:51820
# 172.16.0.0/12 — весь диапазон docker-мостов: без него ответ LM Studio не найдёт дорогу назад
# через туннель (см. деплой-план, критичная деталь маршрутизации).
AllowedIPs = 10.8.0.0/24, 172.16.0.0/12
PersistentKeepalive = 25
-----8<-----------------------------------8<-----

   После подключения туннеля в LM Studio (Developer -> Local Server) сервер должен слушать
   0.0.0.0:1234, а в брандмауэре Windows — разрешить входящие на 1234 для профиля WireGuard.

4. Домен: A-записи gp-family.ru/www -> ${SERVER_IP}; seq/s3/admin.gp-family.ru -> 10.8.0.1
   (приватный адрес в публичной DNS-зоне — нормально, резолвится в никуда без VPN).

5. Полная инструкция, включая установку корневого CA Caddy для админ-поддоменов — deploy/README.md.
============================================================
SUMMARY
