# Docker / infrastructure audit

## [DC-01] Kafka deployment не сохраняет данные и не имеет redundancy

Severity: High

Category: Docker

Location: `docker-compose.yml`

Lines: `71-103,356-360`

Current behavior: Один combined broker/controller, replication factors равны 1. Kafka volume объявлен, но mount закомментирован.

Problem: Broker filesystem — единственная копия commands/events/offsets и исчезает при container recreation.

Risk: Потеря принятых sync/merge commands, DLQ и consumer offsets; recovery может повторить или пропустить destructive операции.

Example scenario: `docker compose down`/recreate Kafka удаляет container layer; pending merge outbox можно переотправить, но уже опубликованные sync commands без DB outbox потеряны.

Recommended solution: Production Kafka cluster ≥3 brokers с persistent volumes, replication/min ISR policy, backups/monitoring и явно созданными topics.

Priority: P1

Confidence: High

## [DC-02] Build context включает repository secrets

Severity: High

Category: Docker

Location: `docker/Dockerfile.service`; repository root

Lines: Dockerfile `1-8`; `.dockerignore` отсутствует

Current behavior: Каждый backend build использует root context и `COPY . .`; tracked `.env*` попадают в build stage/context/cache. Финальный runtime получает только publish output, поэтому наличие env в final layer не подтверждено.

Problem: Secrets передаются Docker daemon/remote builder и остаются в build cache/intermediate layers.

Risk: Утечка через cache export, shared builder или debug of intermediate image.

Example scenario: CI публикует BuildKit cache в registry, содержащий layer после `COPY . .` с `.env.prod`.

Recommended solution: После rotation добавить строгий `.dockerignore`, копировать только solution/project/source inputs и применять BuildKit secrets только на нужном RUN.

Priority: P1

Confidence: High

## [DC-03] RedisInsight и Kafka UI всегда входят в production Compose

Severity: High

Category: Docker

Location: `docker-compose.yml`

Lines: RedisInsight `57-69`; Kafka UI `107-120`; Dozzle `341-354`

Current behavior: Только Dozzle имеет `dev-tools` profile и localhost bind. RedisInsight/Kafka UI запускаются всегда, bind-ятся на все interfaces и не имеют auth config.

Problem: Административные интерфейсы production data plane доступны как обычные services.

Risk: Просмотр/изменение Redis/Kafka состояния и tenant metadata, DoS/command injection через admin capabilities.

Example scenario: Production host firewall разрешает declared ports, внешний клиент открывает Kafka UI и читает merge messages.

Recommended solution: Перенести все UI в dev-only profile, bind localhost/VPN и добавить authentication; production manifest не должен содержать их.

Priority: P1

Confidence: High

## [DC-04] Containers запускаются от root, frontend использует Vite preview

Severity: Medium

Category: Docker

Location: `docker/Dockerfile.service`; `src/frontend-Iframe/Dockerfile.frontend-iframe`

Lines: service `16-32`; frontend `1-20`

Current behavior: `USER` не задан. Frontend final image — полный Node image с dev dependencies и `npm run preview`, а не минимальный static web server.

Problem: Избыточные privileges/package surface и dev-oriented server в production path.

Risk: Больший blast radius RCE, image size/CVEs, менее предсказуемые proxy/timeouts.

Example scenario: Уязвимость application получает root внутри container и полный Node toolchain.

Recommended solution: Multi-stage frontend → immutable static assets в hardened non-root server; .NET runtime запускать dedicated UID с read-only rootfs/cap-drop где возможно.

Priority: P2

Confidence: High

## [DC-05] Health/readiness не отражают реальные зависимости

Severity: Medium

Category: Docker

Location: `docker-compose.yml`; `src/Shared/MsContractor.BuildingBlocks/HealthChecks.cs`; `scripts/health.sh`

Lines: Compose healthchecks `147-152,178-183,204-209,232-237,261-266,288-293,315-320`; script `5-53`

Current behavior: Container health вызывает `/health/live`, хотя dependency-aware checks зарегистрированы как ready лишь у части сервисов. Notification/Audit self-healthy despite no business implementation. Script жёстко использует `.env.dev` и требует Dozzle.

Problem: Orchestrator допускает traffic/start dependencies на процесс, который не готов к Redis/Kafka/PostgreSQL/business consumption.

Risk: Green deployment с неработающим token/sync/merge path.

Example scenario: Redis падает после старта; Egress container health остаётся live/healthy для depends_on consumers.

Recommended solution: Разделить liveness/readiness/startup semantics; Compose/orchestrator должен проверять `/health/ready`, а business consumers — lag/connectivity. Параметризовать health script environment/profile.

Priority: P2

Confidence: High

## [DC-06] Изменение POSTGRES_PORT/REDIS_PORT ломает внутренние listeners

Severity: Medium

Category: Docker

Location: `docker-compose.yml`; `.env.dev`; `.env.prod`

Lines: Compose `7-8,29-30,45-46`; env ports `5-6`

Current behavior: Одна переменная используется и как host port, и как container/internal port. Images PostgreSQL/Redis не перенастраивают daemon listen port.

Problem: Значения кроме defaults создают connection strings на port, где процесс внутри container не слушает.

Risk: Непонятный startup outage при обычной попытке избежать host port collision.

Example scenario: `POSTGRES_PORT=15432` публикует `15432:15432`, но PostgreSQL слушает 5432.

Recommended solution: Разделить `*_HOST_PORT` и фиксированный internal port либо явно конфигурировать daemon listener.

Priority: P2

Confidence: High
