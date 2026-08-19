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

RUN apk --no-cache add iproute2 iptables bash
COPY --from=tools /amneziawg-tools/src/wg /usr/bin/awg
COPY --from=tools /amneziawg-tools/src/wg-quick/linux.bash /usr/bin/awg-quick
RUN chmod +x /usr/bin/awg /usr/bin/awg-quick && \
    ln -s /usr/bin/awg /usr/bin/wg && \
    ln -s /usr/bin/awg-quick /usr/bin/wg-quick
COPY --from=awg /usr/bin/amneziawg-go /usr/bin/amneziawg-go

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
