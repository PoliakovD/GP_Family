#!/bin/bash
# Поднимает интерфейс awg0 из смонтированного /etc/amnezia/amneziawg/awg0.conf (см.
# docker-compose.prod.yml, volume ./wg:/etc/amnezia/amneziawg:ro) и держит контейнер живым.
#
# awg-quick (как и апстримный wg-quick) детачит userspace-процесс amneziawg-go в фоне и сам
# завершается сразу после настройки интерфейса — PID 1 контейнера должен пережить его, но не
# быть "фальшиво живым", если процесс упадёт: пока интерфейс виден в `awg show interfaces`,
# ждём; как только пропал — выходим ненулевым кодом, чтобы restart: unless-stopped докера
# реально пересоздал контейнер, а не оставил его висеть без реального тоннеля.
set -euo pipefail

awg-quick up awg0

while awg show interfaces | grep -qw awg0; do
    sleep 5
done

echo "Интерфейс awg0 пропал — выходим, чтобы docker перезапустил контейнер по restart policy." >&2
exit 1
