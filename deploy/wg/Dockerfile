# Сайдкар AmneziaWG-клиента для сплит-туннеля бота к api.telegram.org (ADR-0008). Сборочные
# стадии 1-3 — дословная адаптация ОФИЦИАЛЬНОГО Dockerfile проекта
# (https://github.com/amnezia-vpn/amneziawg-go/blob/master/Dockerfile), сверено вручную с
# исходником на момент написания — не придумано с нуля. Добавлена только стадия entrypoint.sh
# (у апстрима нет CMD/ENTRYPOINT вообще, это просто базовый образ с бинарями).
#
# ⚠️ Перед использованием в проде: соберите и проверьте локально/на тестовом VPS с РЕАЛЬНЫМ
# конфигом клиента (getMe через тоннель должен проходить, см. health/telegram у бота) — этот
# Dockerfile не тестировался против вашего конкретного Amnezia-сервера.
#
#   docker build -f deploy/wg/Dockerfile -t familyhub-wg-client deploy/wg

FROM golang:1.25.12 AS awg
ARG AMNEZIAWG_GO_COMMIT=master
RUN git clone https://github.com/amnezia-vpn/amneziawg-go.git /awg && \
    cd /awg && git checkout ${AMNEZIAWG_GO_COMMIT}
WORKDIR /awg
RUN go mod download && \
    go mod verify && \
    go build -ldflags '-linkmode external -extldflags "-fno-PIC -static"' -v -o /usr/bin

FROM alpine:3.19 AS tools
# Зафиксируйте конкретный тег/коммит после первой успешной сборки — "master" воспроизводим
# только на момент сборки, не навсегда (тот же принцип, что и AMNEZIAWG_GO_COMMIT выше).
ARG AWGTOOLS_COMMIT=master
RUN apk add --no-cache git build-base linux-headers && \
    git clone https://github.com/amnezia-vpn/amneziawg-tools.git /amneziawg-tools && \
    cd /amneziawg-tools && git checkout ${AWGTOOLS_COMMIT} && \
    cd src && make

FROM alpine:3.19

# openresolv — даёт /sbin/resolvconf: awg-quick (linux.bash) вызывает его для DNS = из
# [Interface]-секции конфига; без пакета команда не найдена, awg-quick падает и откатывает
# интерфейс (ip link delete) ещё до healthcheck — рестарт-луп. systemd тут не нужен, openresolv
# сам пишет /etc/resolv.conf.
RUN apk --no-cache add iproute2 iptables bash openresolv
COPY --from=tools /amneziawg-tools/src/wg /usr/bin/awg
COPY --from=tools /amneziawg-tools/src/wg-quick/linux.bash /usr/bin/awg-quick
# add_default() в этом форке безусловно дёргает `cmd sysctl -q net.ipv4.conf.all.src_valid_mark=1`
# (в отличие от апстримного wg-quick — тут нет проверки "уже 1?" перед записью) и `cmd` считает
# провал фатальным: интерфейс откатывается (down) и entrypoint.sh уходит в рестарт-луп. Docker
# уже выставляет этот sysctl при создании контейнера через `sysctls:` в docker-compose.prod.yml —
# но net.ipv4.conf.all.* можно менять так только на этапе создания контейнера (привилегированным
# действием самого Docker) или в --privileged; обычный write() изнутри контейнера с одним лишь
# NET_ADMIN упирается в "Read-only file system", даже когда значение уже верное. Раз оно и так
# выставлено через compose, здесь достаточно не считать повторную (заведомо обречённую) попытку
# фатальной.
RUN sed -i \
    "s/cmd sysctl -q net.ipv4.conf.all.src_valid_mark=1/sysctl -q net.ipv4.conf.all.src_valid_mark=1 || true/" \
    /usr/bin/awg-quick
RUN chmod +x /usr/bin/awg /usr/bin/awg-quick && \
    ln -s /usr/bin/awg /usr/bin/wg && \
    ln -s /usr/bin/awg-quick /usr/bin/wg-quick
COPY --from=awg /usr/bin/amneziawg-go /usr/bin/amneziawg-go

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
