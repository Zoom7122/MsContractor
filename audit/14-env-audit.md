# Environment/configuration audit

Значения секретов намеренно не приводятся. Источники: `.env.dev`, `.env.prod`, `docker-compose.yml`, `appsettings.json`, frontend Vite config и test-data script.

## Матрица переменных Compose

| Переменная | Где задаётся | Где читается/применяется | Статус |
|---|---|---|---|
| `POSTGRES_DB` | dev/prod env | PostgreSQL image, connection string, health script | используется |
| `POSTGRES_USER` | dev/prod env | PostgreSQL image, connection string, health script | используется; общий DB principal |
| `POSTGRES_PASSWORD` | dev/prod env | PostgreSQL image, общий service env | используется; секрет в Git, `C-01` |
| `POSTGRES_PORT` | dev/prod env | host mapping и internal connection string | используется; двойная semantics, `DC-06` |
| `REDIS_PORT` | dev/prod env | host mapping и internal connection string | используется; двойная semantics, `DC-06` |
| `KAFKA_PORT` | dev/prod env | external listener/host mapping | используется |
| `KAFKA_INTERNAL_PORT` | dev/prod env | broker listener, apps, health | используется |
| `KAFKA_CONTROLLER_PORT` | dev/prod env | KRaft controller | используется |
| `KAFKA_UI_PORT` | dev/prod env | Kafka UI host mapping | используется; production exposure `C-04/DC-03` |
| `KAFKA_UI_INTERNAL_PORT` | dev/prod env | Kafka UI container mapping | используется |
| `SERVICE_INTERNAL_PORT` | dev/prod env | все ASP.NET listeners/URLs/health | используется |
| `GATEWAY_BFF_PORT` | dev/prod env | Gateway host mapping/health | используется |
| `VENDOR_SERVICE_PORT` | dev/prod env | не найдено в Compose/code | не используется, `E-01` |
| `MOYSKLAD_EGRESS_SERVICE_PORT` | dev/prod env | host mapping/health | используется; internal exposure `C-03` |
| `CATALOG_SYNC_SERVICE_PORT` | dev/prod env | host mapping/health | используется; internal exposure `C-03` |
| `DUPLICATES_MERGE_SERVICE_PORT` | dev/prod env | host mapping/health | используется; internal exposure `C-03` |
| `NOTIFICATION_SERVICE_PORT` | dev/prod env | host mapping/health | используется, хотя сервис placeholder |
| `AUDIT_SERVICE_PORT` | dev/prod env | host mapping/health | используется, хотя сервис placeholder |
| `FRONTEND_IFRAME_PORT` | только Compose fallback | frontend host mapping | отсутствует в env; fallback 4174 |
| `DOZZLE_PORT` | dev env; Compose fallback | Dozzle host mapping/health | dev используется; prod отсутствует |
| `VITE_ALLOWED_HOSTS` | dev/prod env | frontend container → Vite config | используется; prod значение совпадает с dev и содержит dev tunnel artifact |
| `INTERNAL_API_KEY` | dev/prod env | `InternalApi__Key` всех backend | используется; секрет в Git и чрезмерно распространён, `C-01/S-04` |
| `MOYSKLAD_VENDOR_APP_ID` | dev/prod env | Vendor options | используется |
| `MOYSKLAD_VENDOR_APP_UID` | dev/prod env | Vendor options | используется |
| `MOYSKLAD_VENDOR_SECRET` | dev/prod env | Vendor JWT validation/factory | используется; секрет в Git, `C-01` |
| `VENDOR_TOKEN_ENCRYPTION_KEY` | dev/prod env | AES-GCM protector | используется; секрет в Git, `C-01` |
| `DEV_ACCOUNT_ID` | только dev env; обязательный Compose substitution | Vendor dev session option | prod отсутствует, но Compose требует; `C-02` |

## Переменные, создаваемые Compose/.NET mapping

| Configuration key/env | Реальный consumer | Замечание |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | все backend | всегда `Development`, `C-02` |
| `ASPNETCORE_URLS` | все backend | используется |
| `ConnectionStrings__Postgres` | Vendor, Catalog, Duplicates; optional startup probe Egress | передаётся также Gateway/Notification/Audit без необходимости |
| `Redis__ConnectionString` | Vendor, Gateway, Egress | передаётся всем services; Merge declared dependency Redis, но code limiter/session не использует его напрямую |
| `Kafka__BootstrapServers` | Catalog, Duplicates | передаётся всем services |
| `Kafka__SyncConsumerGroup` | Catalog | используется |
| `Kafka__MergeConsumerGroup` | не задаётся в env | default `duplicates-merge-service` в code |
| `Services__VendorService__BaseUrl` | Egress | plaintext internal HTTP |
| `Services__MoySkladEgressService__BaseUrl` | Catalog, Duplicates | используется |
| `Services__CatalogSyncService__BaseUrl` | Gateway | используется |
| `Services__DuplicatesMergeService__BaseUrl` | Gateway | используется |
| `Services__GatewayBff__BaseUrl` | consumer не найден | общий env noise, `E-01` |
| `Services__NotificationService__BaseUrl` | consumer не найден | общий env noise, `E-01` |
| `Services__AuditService__BaseUrl` | consumer не найден | общий env noise, `E-01` |
| `MoySklad__JsonApiBaseUrl` | Egress | используется |
| `Vendor__TokenKeyVersion` | Vendor options | Compose constant `1` |
| `VITE_BACKEND_PROXY_TARGET` | `vite.config.js` во время container start | используется Vite proxy |
| `VITE_API_BASE_URL` | frontend build/import.meta | Docker build ARG default пустой; Compose build arg не задаёт, same-origin |
| `VITE_DEV_HOST`, `VITE_PREVIEW_HOST` | Vite config | поддерживаются, но env/Compose не задают |
| `Merge__MaxOperationAttempts` | MergeProcessor | env не задаёт; appsettings/default code = 5 |
| `Session__CookieName` | Gateway | appsettings/default, env не задаёт |

## Переменные test-data

| Переменная | Использование | Статус |
|---|---|---|
| `MS_LOGIN` | Basic Auth test-data script | используется |
| `MS_PASSWORD` | Basic Auth test-data script | используется; секрет в Git, `C-01` |
| `MS_BASE_URL` | JSON API base URL | используется/default official API |
| `MS_COUNTERPARTY_COUNT` | объём создаваемых данных | используется |
| `MS_WRITE_REPORT_JSON` | запись локального отчёта | используется |
| `MS_DOCUMENT_TYPES` | выбор типов документов | используется |
| `MS_REQUEST_DELAY_SECONDS` | throttling script | используется |
| `TEST_REDIS_CONNECTION` | Vendor integration test | optional; без неё test молча проходит, `T-03` |

## [E-01] Env содержит неиспользуемые и чрезмерно общие переменные

Severity: Low

Category: Code Quality

Location: `.env.dev`; `.env.prod`; `docker-compose.yml`

Lines: env `16`; Compose `12,17-18` и общий anchor `3-18`

Current behavior: `VENDOR_SERVICE_PORT` не используется; Gateway/Notification/Audit base URLs не читаются; весь anchor инжектируется каждому service.

Problem: Configuration surface не отражает реальные зависимости и затрудняет least-privilege audit.

Risk: Ошибочное изменение переменной без эффекта или вера в несуществующую связь/health path.

Example scenario: Оператор меняет `VENDOR_SERVICE_PORT`, ожидая host publication, но route не появляется.

Recommended solution: Удалять stale variables после отдельного change review; формировать service-specific env blocks и config validation.

Priority: P3

Confidence: High

## [E-02] appsettings содержат weak local database fallback

Severity: Low

Category: Security

Location: `src/Services/VendorService/appsettings.json`; `src/Services/CatalogSyncService/appsettings.json`

Lines: Vendor `2-4`; Catalog `8-10`

Current behavior: Committed default connection strings используют предсказуемые local credentials. В Compose они переопределяются; использование этих defaults в production не подтверждено.

Problem: Misconfigured standalone deployment может молча подключиться с shared weak credential вместо fail-fast.

Risk: Низкая — обычно только local; повышается при публикации default-configured DB.

Example scenario: Service запускают вне Compose без secret injection рядом с БД, где оставлена такая же default учётная запись.

Recommended solution: Development secrets/profile отдельно; production startup должен требовать непустую externally supplied connection string и dedicated principal.

Priority: P3

Confidence: High
