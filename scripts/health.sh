#!/usr/bin/env bash

set -uo pipefail

readonly ENV_FILE=".env.dev"
readonly COMPOSE=(docker compose --env-file "$ENV_FILE" --profile dev-tools)

# Docker Compose consumes the same file; loading it here keeps host health URLs
# and PostgreSQL credentials in sync without duplicating values.
set -a
# shellcheck disable=SC1091
. "$ENV_FILE"
set +a

failed=0

check() {
  local name="$1"
  shift

  if "$@" >/dev/null 2>&1; then
    printf '[OK] %s\n' "$name"
  else
    printf '[FAIL] %s\n' "$name"
    failed=$((failed + 1))
  fi
}

check_http() {
  local name="$1"
  local port="$2"
  check "$name" curl --fail --silent --show-error --connect-timeout 3 --max-time 10 \
    "http://localhost:${port}/health/ready"
}

check_http gateway-bff "$GATEWAY_BFF_PORT"
check vendor-service "${COMPOSE[@]}" exec -T vendor-service \
  curl --fail --silent --show-error --connect-timeout 3 --max-time 10 \
  "http://127.0.0.1:${SERVICE_INTERNAL_PORT}/health/ready"
check_http moysklad-egress-service "$MOYSKLAD_EGRESS_SERVICE_PORT"
check_http catalog-sync-service "$CATALOG_SYNC_SERVICE_PORT"
check_http duplicates-merge-service "$DUPLICATES_MERGE_SERVICE_PORT"
check_http notification-service "$NOTIFICATION_SERVICE_PORT"
check_http audit-service "$AUDIT_SERVICE_PORT"

check postgres "${COMPOSE[@]}" exec -T postgres \
  pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"
check redis bash -o pipefail -c \
  "${COMPOSE[*]} exec -T redis redis-cli ping | grep -qx PONG"
check kafka "${COMPOSE[@]}" exec -T kafka \
  /opt/kafka/bin/kafka-topics.sh --bootstrap-server "localhost:${KAFKA_INTERNAL_PORT}" --list
check dozzle curl --fail --silent --show-error --connect-timeout 3 --max-time 10 \
  "http://localhost:${DOZZLE_PORT}/healthcheck"

if (( failed == 0 )); then
  printf 'Health check passed\n'
  exit 0
fi

printf 'Health check failed: %d components unavailable\n' "$failed"
exit 1
