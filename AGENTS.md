# MSContractor

## Project Overview

MSContractor is a multi-tenant integration with МойСклад. The backend is a .NET 10 solution (`MsContractor.sln`), the iframe UI is Vue 3/Vite under `src/frontend-Iframe`, and local infrastructure is defined in `docker-compose.yml`.

The solution contains five services, two shared libraries, and two xUnit test projects:

- `src/Services/{Gateway.Bff,VendorService,MoySkladEgressService,CatalogSyncService,DuplicatesMergeService}`;
- `src/Shared/MsContractor.Contracts` for shared HTTP/Kafka DTOs and `MsContractor.BuildingBlocks` for cross-cutting infrastructure;
- `tests/MsContractor.Sync.Tests` and `tests/MsContractor.VendorService.Tests`.

Read the implementation, contracts, tests, configuration, and current diff before changing anything. The root `README.md` describes the backend layers and development commands; verify commands against the actual `Makefile`.

## Architecture

Primary flow:

```text
Vue iframe -> Gateway BFF -> Vendor / Catalog Sync / Duplicates Merge
                                  |             |
                                  +-----> MoySklad Egress -> MoySklad JSON API 1.2
                                                   |
                                                   +-> Vendor internal token endpoint

Catalog Sync / Duplicates Merge <-> Kafka
Catalog Sync / Duplicates Merge -> PostgreSQL catalog_sync schema
Vendor -> PostgreSQL vendor schema + Redis sessions/replay protection
Gateway -> Redis session reads
Egress -> PostgreSQL egress operation journal + Redis dependency; current rate-limit hook is observation-only
```

Public requests enter through Gateway. Internal HTTP endpoints require `X-Internal-Api-Key` and explicit trusted context headers. Async state belongs in PostgreSQL; Kafka carries commands/events, not authoritative operation state.

## Service Responsibilities

| Component | Responsibility |
| --- | --- |
| `Gateway.Bff` | Session validation, trusted account/user context, routing, and small BFF read orchestration. |
| `VendorService` | MoySklad Vendor API activation/deactivation, context/session lifecycle, token ownership and encryption. |
| `MoySkladEgressService` | The only outbound gateway to MoySklad JSON API 1.2; token retrieval, HTTP mapping, pagination and rate-limit hooks. |
| `CatalogSyncService` | Full/incremental counterparty synchronization, durable sync runs/watermarks/catalog, Kafka consumer and sync-event outbox. |
| `DuplicatesMergeService` | Duplicate preview, selection preview, durable merge jobs/operations, merge outbox/consumer, main update and duplicate archive. |
| `frontend-Iframe` | Vue iframe UI; calls `/api/*` with session cookies through Gateway. |

`DuplicatesMergeService` currently references `CatalogSyncService` and uses its `CatalogSyncDbContext`/`catalog_sync` schema. Treat this as an explicit current coupling, not permission for new services to read another service's private persistence.

## Backend Layers

Use only populated layers: `Controllers`, `Consumers`, `Services`, `Repositories`, `Persistence`, `Clients`, `Gateways`, `Messaging`, `Contracts`, `Models`, `Middleware`, `HealthChecks`. Namespaces follow directories. Options live in `Models/Options`; exceptions shared across layers live in `Models/Exceptions`. Interfaces stay next to their layer implementations.

Business services, controllers, consumers and publishers must not use EF or DbContext directly. Repositories own materialized queries and atomic persistence methods; EF transactions do not escape them. Infrastructure health checks and startup may access dependencies directly. Redis I/O belongs in repositories, while session/JWT rules stay in services. Consumers own offsets and retries; publishers and DLQ publication belong in Messaging.

New classes with dependencies, especially controllers, services, gateways and repositories, must use an explicit constructor with private fields for dependency assignment. Do not use C# primary-constructor syntax for these classes. DTOs and property-based persistence models may use parameterless/property initialization when their layer requires it.

Catalog and Merge repository registrations are scoped and use the same CatalogSyncDbContext within an operation. Merge still references CatalogSync for its persistence model. Preserve the full-sync cleanup boundary, incremental read/write transaction, job/operations/outbox atomicity and intermediate merge save points. The incremental repository accepts the service's apply-row function to keep business rules outside persistence without moving reads out of the transaction.

## Critical Architecture Rules

1. Scope every tenant-dependent read, write, lock, cache key, message, and log by `account_id`.
2. Never trust an `account_id` supplied by the frontend. Derive it from the Vendor-created session at Gateway and propagate it as internal context.
3. Only `MoySkladEgressService` may call MoySklad JSON API 1.2. Sync, Merge, Catalog, Gateway, and frontend must not bypass Egress.
4. The access token is owned by Vendor Service. It may be decrypted only there and returned only to authenticated Egress internal calls for immediate outbound use.
5. Persist business/operation state in PostgreSQL. Kafka and Redis are not business databases.
6. Preserve idempotency and failure ordering in Sync/Merge. Do not acknowledge Kafka work before required durable state is committed.
7. Prefer the smallest change. Do not duplicate an existing flow, rename contracts/topics/tables for style, or perform unrelated refactoring.

## Account Isolation

- Gateway reads `mscontractor.session` from Redis and obtains trusted `AccountId` and `EmployeeId`; controllers use those values when calling internal services.
- Internal handlers must reject empty/missing context and include `account_id` in every tenant query. IDs alone are not a tenant boundary.
- PostgreSQL uniqueness/indexes for tenant data must include `AccountId` where appropriate. Foreign keys must not permit cross-account relationships.
- Kafka message keys are currently account IDs for sync/merge ordering. Validate the account in the payload against durable state.
- Redis keys for account-scoped coordination must include the account ID; add user ID only when the policy genuinely differs by user.
- Tests for tenant logic must prove that an ID from account A cannot be read or mutated through account B.

## MoySklad Integration

- JSON API base URL is `https://api.moysklad.ru/api/remap/1.2/`; all calls go through typed clients in Egress.
- Vendor Service owns the token encrypted at rest with AES-GCM (`AccessTokenProtector`) in `vendor.installations`. Egress requests it through `GET /internal/vendor/installations/{accountId}/token`, uses it transiently, and must never store it.
- Never send the token through Kafka or to the frontend; never log it, JWTs, cookies, `Authorization`, secrets, encryption material, or response bodies containing sensitive data.
- Forward `CancellationToken`, use the configured typed `HttpClient` timeout, and preserve controlled upstream error mapping. Do not add blind retries for non-idempotent writes.
- Rate limiting belongs in Egress and must cover every MoySklad request. Scope at least by `account_id`, optionally `account_id + user_id`, plus a global safety limit. Observe `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-Lognex-Reset`, `X-Lognex-Retry-After`, `X-Lognex-Retry-TimeInterval`, and HTTP 429. Merge writes must have priority over background/full-sync reads.
- Important current gap: `ObservingMoySkladRateLimiter` only logs a subset of headers and `WaitAsync` is a no-op; Redis enforcement, global limits, write priority, and `X-Lognex-Retry-TimeInterval` handling are not implemented. Do not claim otherwise.

## Kafka Rules

Contracts live in `src/Shared/MsContractor.Contracts`:

- `mscontractor.commands.sync`: `SyncRequested`;
- `mscontractor.events.sync`: `SyncCompleted` / `SyncFailed` from the catalog outbox;
- `mscontractor.commands.merge`: `MergeRequested` from the merge outbox;
- `mscontractor.commands.merge.dlq`: `MergeDeadLetter`.

Kafka is for asynchronous business commands/events. It is not a database, an access-token transport, or a default transport for large payloads. Keep schema compatibility explicit and carry/validate `message_id`, `correlation_id`, `account_id`, and operation identifiers.

Consumers use `EnableAutoCommit=false`. Design for duplicate delivery: check inbox/durable state first, make transitions idempotent, and commit the offset only after a durable success/terminal failure or a successfully published DLQ record. On retryable/non-durable failure, do not commit; cancellation must stop processing. Keep outbox writes in the same transaction as the business transition they announce.

## PostgreSQL Rules

- `EgressDbContext` owns schema `egress`: `salesreturn_operations`, `salesreturn_claims`. Salesreturn recreation persists intent before remote writes and uses a stable syncId on creation retries. Its recovery worker uses scoped repositories. Merge invokes recreation after ordinary documents, persists the immutable request on its operation and atomically applies confirmed new document IDs; discovery supplies complete salesreturn positions.
- `VendorDbContext` owns schema `vendor`: `installations`, `outbox_messages`.
- `CatalogSyncDbContext` owns schema `catalog_sync`: `sync_runs`, `sync_watermarks`, `inbox_messages`, `counterparties`, `outbox_messages`, `merge_jobs`, `merge_operations`.
- Egress, Vendor, Catalog Sync, and Duplicates Merge call `Database.MigrateAsync()` at startup. Migration files live under each owning DbContext's `Persistence/Migrations` directory. No repository-local `dotnet-ef` tool or documented migration-generation command exists; do not invent one. Verify/install an agreed matching EF tool before generating, and target the owning project/context explicitly.
- Include `account_id` in tenant rows, filters, indexes, alternate/unique keys, and relationships as required. Review query plans/indexes for new access paths.
- Use transactions when job/operation/outbox/inbox state must change atomically. Never edit migration history or another service's schema by hand.
- Do not introduce cross-service database reads without an explicit architectural decision. The existing Merge-to-Catalog context reference is a known exception, not a pattern to expand.

## Redis Rules

Current Redis uses are Vendor session storage/revocation, Vendor JWT replay protection, Gateway session reads, and the Egress rate-limit dependency/hook. Sessions are stored under hashed-token keys; never store or log the raw cookie as a Redis key.

Redis is appropriate for distributed rate limiting, `blocked_until`, short-lived coordination/locks, replay protection, and temporary state. Apply TTLs and account-scoped keys. It must not replace PostgreSQL for merge jobs, sync runs, attempts, compensation state, or other durable business state.

## HTTP Rules

Public Gateway routes are:

- `POST /api/sync`, `POST /api/sync/incremental`;
- `POST /api/merge-preview`, `POST /api/merge-preview/selection`, `POST /api/merge-jobs`;
- YARP proxy routes `/api/moysklad/vendor/**` and `/api/moysklad/session/**` to Vendor Service.

Internal boundaries include `POST /internal/sync`; merge preview/job endpoints under `/internal/merge-*`; Egress counterparties under `/internal/accounts/{accountId}/counterparties`; document discovery under `/internal/accounts/{accountId}/documents/discover`; and Vendor token retrieval under `/internal/vendor/installations/{accountId}/token`.

Use existing typed/named `HttpClient` registrations, `CancellationToken`, bounded timeouts, `ResponseHeadersRead` where established, stable safe errors, and `X-Correlation-Id`. Internal calls use `X-Internal-Api-Key` plus the required `X-Account-Id` or route account, `X-User-Id`, `X-Sync-Run-Id`, `X-Merge-Job-Id`, and `X-Merge-Operation-Id` context. Never construct `HttpClient` per request. Do not retry non-idempotent POST/DELETE/unsafe writes unless idempotency is proven end to end.

Gateway may aggregate BFF reads but must not absorb Sync/Merge business logic. A successful asynchronous start returns `202 Accepted`; completion is not implied.

## Security

- Never commit or log access tokens, `Authorization`, JWTs, cookies, Vendor secrets, passwords, encryption keys, DB credentials, internal API keys, or sensitive business payloads.
- Secrets come from environment/secret management, not committed appsettings, Dockerfiles, frontend bundles, Kafka, or source constants. Never print `.env.dev` or `.env.prod` values in diagnostics.
- Internal API keys are checked with fixed-time comparison; preserve internal authentication on every internal endpoint.
- Keep session cookies `HttpOnly`, `Secure`, `SameSite=None`, and scoped as currently configured unless a security review says otherwise.
- Development-only session creation (`POST /api/moysklad/session/dev`), Swagger/OpenAPI UI, debug endpoints, and test credentials must never become implicitly available in production.
- `make compose-up-prod` loads `.env.prod`, sets `ASPNETCORE_ENVIRONMENT=Production`, and excludes RedisInsight. It stops the already-running dev tool first because disabling a Compose profile does not stop existing containers. `make compose-down-prod` removes containers without deleting named volumes.
- `DEV_ACCOUNT_ID` is optional in Compose and defaults to the empty GUID; Vendor validates it when running in Development. The dev-session route returns `404` outside Development before creating a session or cookie.
- This Compose mode is not a certification of the whole stack for public production deployment; review production secrets, network exposure, persistence, and operations separately.

## Logging and Observability

Use injected `ILogger` and structured templates, for example:

```csharp
logger.LogInformation(
    "Sync started account_id={AccountId} sync_run_id={SyncRunId}",
    accountId,
    syncRunId);
```

Use scopes/fields already present in the flow: `correlation_id`, `account_id`, `operation_id`, `sync_run_id`, `merge_job_id`, `message_id`, offsets, attempt counts, durations, safe status/error codes. Avoid string interpolation for structured values and never log secret headers, cookies, tokens, sensitive bodies, or raw upstream error bodies. Do not copy the existing startup `Console.WriteLine` calls in Vendor Service; new application logging uses `ILogger`.

## Development Environment

Prerequisites reflected by the repository are .NET SDK 10, Docker with Compose, Node 20/npm, `curl`, and Bash. Development uses `.env.dev`; `make compose-up-prod` uses `.env.prod` for the Production application mode, but the Compose stack is not thereby certified for public production deployment.

Default host ports in `.env.dev`: Gateway `8080`, Egress `8082`, Catalog `8083`, Duplicates `8084`, frontend preview `4174`, PostgreSQL `5432`, Redis `6379`, Kafka `29092`, and RedisInsight `5540`. `VENDOR_SERVICE_PORT=8081` exists in the env file, but Compose does not publish Vendor Service to the host. Caddy is configured separately on `8088` and is not a Compose service.

## Build and Run

Run from the repository root:

```bash
dotnet restore MsContractor.sln
dotnet build MsContractor.sln
make compose-up                 # Development with dev-tools
docker compose --env-file .env.dev --profile dev-tools ps
make logs
make recreate SERVICE=catalog-sync-service
make compose-down
make compose-up-prod            # Production; stops dev-tools first
make compose-down-prod          # Stops/removes containers, preserves volumes
```

Validate Compose after configuration changes:

```bash
docker compose --env-file .env.dev --profile dev-tools config
docker compose --env-file .env.prod config
```

Frontend production build:

```bash
cd src/frontend-Iframe
npm ci
npm run build
```

`make compose-down-del` deletes Compose named volumes and is destructive. `make compose-down-prod` preserves them. RedisInsight belongs to profile `dev-tools`; use `make compose-up` for the full dev stack. Do not use nonexistent README targets `make compose`, `make ps`, or `make build`.

## Tests

Run all solution tests with the real Make target:

```bash
make test
```

Targeted suites:

```bash
dotnet test tests/MsContractor.Sync.Tests/MsContractor.Sync.Tests.csproj
dotnet test tests/MsContractor.VendorService.Tests/MsContractor.VendorService.Tests.csproj
```

There is currently no frontend automated-test script; at minimum run `npm run build`. Add unit tests for business logic and integration-style tests for persistence, HTTP, and Kafka. Fake external HTTP. For affected critical flows cover account isolation, duplicate messages/idempotency, failure, cancellation, retries where explicitly allowed, Egress 429, and Merge partial failure/ordering/compensation.

## Health Checks

Every backend maps JSON endpoints `/health/live`, `/health/ready`, and compatibility liveness `/health`. Docker probes `/health/live`; `scripts/health.sh` probes readiness and infrastructure.

After the dev stack is running:

```bash
make health
```

The script checks Gateway and published services on `.env.dev` ports, Vendor inside its container, PostgreSQL, Redis, and Kafka. Vendor readiness checks PostgreSQL+Redis; Catalog checks PostgreSQL+Kafka; Egress checks PostgreSQL (including pending migrations) + Redis. Other services currently expose only the shared self check, so their readiness does not prove undeclared dependencies.

## Docker

Compose project name is `mscontractor`. Service key -> fixed container name:

```text
postgres -> mscontractor-postgres
redis -> mscontractor-redis
redis-insight -> mscontractor-redis-insight
kafka -> mscontractor-kafka
gateway-bff -> mscontractor-gateway-bff
vendor-service -> mscontractor-vendor-service
moysklad-egress-service -> mscontractor-moysklad-egress-service
catalog-sync-service -> mscontractor-catalog-sync-service
duplicates-merge-service -> mscontractor-duplicates-merge-service
frontend-iframe -> mscontractor-frontend-iframe
```

Use service keys, not container names, in Compose commands and internal DNS. Do not expose internal services without need or casually change production infrastructure. RedisInsight belongs only to profile `dev-tools`; never enable it in production. The production targets preserve named data volumes.

## Documentation Sources

Check the implementation and tests first, then authoritative upstream documentation:

- [MoySklad Vendor API 1.0](https://dev.moysklad.ru/doc/api/vendor/1.0/)
- [MoySklad JSON API 1.2](https://dev.moysklad.ru/doc/api/remap/1.2/)
- [MoySklad developer portal](https://dev.moysklad.ru/)
- [Apache Kafka documentation](https://kafka.apache.org/documentation/)
- [Confluent .NET client](https://docs.confluent.io/kafka-clients/dotnet/current/overview.html)
- [Kafka consumer design](https://docs.confluent.io/kafka/design/consumer-design.html)
- [Redis rate limiting](https://redis.io/docs/latest/develop/use-cases/rate-limiter/)
- [Redis distributed locks](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/)

## Change Workflow

1. Inspect `git status`/`git diff`; preserve unrelated user changes.
2. Find the existing implementation and trace the full frontend/Gateway/internal HTTP/Kafka/Egress/persistence flow as applicable.
3. Read related contracts, tests, configuration, migrations, and health checks.
4. State any assumption that changes scope; prefer a minimal implementation over parallel mechanisms or broad refactors.
5. Keep public/shared contracts compatible unless the task explicitly requires a coordinated versioned change.
6. Add success and failure-path tests close to the existing test suite.
7. Build/test, validate Compose if touched, and run health when runtime behavior is affected.
8. Re-read the final diff for secrets, generated/temp files, accidental production changes, account-isolation leaks, and direct MoySklad calls outside Egress.

For Merge changes, preserve preview -> durable pending job/ordered operations + outbox -> Kafka consumer -> update main -> update local main -> archive duplicates -> update each local result -> terminal/partial state. A failed main update must not archive duplicates. Model attempts and idempotency durably; archive only after prerequisite document/other operations succeed. Locks, document operations, and compensation are required design concerns for extensions, but are not implemented by the current merge schema/processor—do not pretend they exist or add ad-hoc in-memory substitutes.

## Multi-agent Review

- Do not spawn subagents automatically. Use `coder`, `reviewer`, and `architect` only when the user explicitly requests multi-agent work or names one of these agents.
- When the user says `full agent`, run the full pipeline: `coder -> reviewer -> architect`. Send actionable reviewer findings back to the coder; after fixes, rerun reviewer and architect.
- After the full pipeline passes, run Crit on the final code diff as an additional review step. Let the global Codex Stop hook open Crit; if it does not open, invoke `$crit` manually from the project root. Do not start a second Crit session while the automatic review is open. Address inline comments and continue the Crit review loop until the user approves or finishes a round without new comments.

## Definition of Done

- The intended projects compile and `dotnet build MsContractor.sln` succeeds.
- Relevant tests and normally `make test` pass; frontend changes also pass `npm run build`.
- New business logic and failure paths are tested at the appropriate level.
- Compose config validates if Docker/config changed; `make health` passes when runtime behavior is in scope.
- The diff contains only task-related files, no secrets/generated artifacts, and preserves user work.
- Trusted account isolation, cancellation, idempotency, durable state/offset ordering, and safe logging remain intact.
- No MoySklad request bypasses Egress and no token escapes its permitted Vendor-to-Egress transient path.
- API/Kafka/database compatibility and migration ownership were reviewed.

## Forbidden Changes

- Trusting frontend `account_id`, omitting tenant filters, or introducing cross-account keys/relationships.
- Direct MoySklad JSON API calls from Gateway, Sync, Merge, Catalog, or frontend.
- Persisting access tokens outside encrypted Vendor storage; putting tokens/secrets in frontend, Kafka, logs, source, Dockerfiles, or committed settings.
- Treating Kafka/Redis as authoritative operation storage, auto-committing before durable processing, or ignoring duplicate delivery.
- Blind retries of unsafe writes, swallowing cancellation, unbounded HTTP calls, or per-request `new HttpClient()`.
- Archiving merge duplicates before the main/prerequisite operations succeed, losing partial-failure detail, or replacing durable attempts/compensation with memory state.
- Reading/writing another service's private schema without an explicit architecture decision.
- Enabling dev session, Swagger/debug endpoints, or test credentials in production by default.
- Renaming existing services, DTOs, topics, routes, schemas, or tables merely for style; adding a second implementation of an existing mechanism.
- Unrelated refactors, destructive volume/database commands without explicit approval, reverting user changes, or committing generated/temp files.
