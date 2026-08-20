# Варианты устранения всех findings

Документ дополняет `01-critical.md`—`14-env-audit.md`. Он не заменяет поле `Recommended solution`, а показывает альтернативы и обоснование рекомендуемого пути. Оценка строится по четырём критериям: закрытие исходного риска, корректность при нескольких instances, возможность поэтапного rollout/rollback и эксплуатационная стоимость.

## Critical

### [C-01] Действующие секреты хранятся в Git

Варианты:

1. Только удалить `.env*` из текущей ветки. Быстро, но секреты останутся в history, forks, CI caches и у прежних получателей.
2. Ротировать значения и оставить history как есть. Немедленно обезвреживает секреты, но старые значения продолжают выглядеть пригодными и могут повторно использоваться.
3. Сначала отозвать/ротировать все значения, затем очистить history, caches и перейти на secret manager с шаблонными `.env.example`.

Предпочтителен вариант 3: только он одновременно прекращает текущую компрометацию и убирает источник повторной утечки. Порядок «rotation до history rewrite» важен, потому что переписывание Git само по себе не отзывает уже скопированный секрет.

### [C-02] Production Compose включает Development и dev-session

Варианты:

1. Поменять общий `ASPNETCORE_ENVIRONMENT` на `Production`. Это закроет action guard, но ухудшит dev workflow и оставит dev route в production binary.
2. Добавить production override с `Production`, а dev action оставить защищённым `IsDevelopment()`.
3. Разнести manifests/profiles и регистрировать dev endpoint/DevSession dependencies только в Development; добавить deployment smoke test на 404.

Предпочтителен вариант 3: route отсутствует физически, а не зависит от одной строки конфигурации. Это fail-closed решение и сохраняет удобный dev profile без production attack surface.

### [C-03] Internal tenant API опубликованы наружу

Варианты:

1. Убрать host port mappings. Это немедленно сокращает поверхность, но любой контейнер общей сети всё ещё может использовать shared key.
2. Оставить ports, ограничить firewall/VPN и выдать отдельные API keys сервисам. Лучше для эксплуатации, но keys остаются bearer credentials без identity binding.
3. Не публиковать ports, сегментировать сети и применять mTLS/workload identity с authorization policy «service → разрешённый action»; tenant context проверять на каждом hop.

Предпочтителен вариант 3, внедряемый поэтапно начиная с варианта 1: он закрывает и внешний доступ, и lateral movement, а также позволяет отозвать один service identity без общей ротации.

### [C-04] Data stores и admin UI доступны с host

Варианты:

1. Bind ports к `127.0.0.1`. Хороший dev quick fix, но недостаточен для remote production host и SSRF/container compromise.
2. Закрыть ports firewall/security groups, сохранив Compose mappings.
3. Удалить production mappings, вынести data plane в private network, включить TLS/auth/ACL и давать admin access только через audited bastion/VPN.

Предпочтителен вариант 3: network isolation и authentication являются независимыми слоями; ошибка одного слоя не открывает все tenant data. Вариант 1 уместен только как немедленная локальная защита.

## Architecture

### [A-01] DuplicatesMergeService владеет моделью CatalogSyncService

Варианты:

1. Оставить общий DbContext, но вынести entities в Shared. Это уменьшит compile dependency, однако закрепит shared database и связанность схем.
2. Объединить CatalogSync и Merge в один deployable bounded context. Честно отражает единое владение БД и проще транзакционно, но уменьшает независимость масштабирования.
3. Дать Merge собственную БД/state и взаимодействовать с Catalog через versioned internal API/events.

Предпочтителен вариант 2 в краткосрочной перспективе, если команды и lifecycle общие; вариант 3 — целевая архитектура только при реальной потребности независимого deploy/scale. Вынесение private entities в Shared не рекомендуется, потому что скрывает, а не устраняет нарушение.

### [A-02] Vendor outbox не публикуется

Варианты:

1. Удалить outbox и признать события неиспользуемыми. Допустимо только если lifecycle events действительно не нужны.
2. Публиковать Kafka прямо из request handler. Быстро, но возвращает dual-write окно DB/Kafka.
3. Реализовать Vendor-owned outbox publisher с claim/lease, retry, idempotent consumers и lag monitoring.

Предпочтителен вариант 3: таблица и транзакционная запись уже существуют, поэтому он завершает начатый reliable pattern без изменения callback semantics. Вариант 2 разрушил бы основное преимущество outbox.

### [A-03] Sync и merge не координируются по account

Варианты:

1. Положить обе команды в один Kafka topic/group с account key. Даёт ordering, но связывает разные workloads и усложняет приоритет merge.
2. Redis lease на account с отдельной priority policy для merge. Быстро и distributed, но требует fencing и чёткой политики при потере Redis.
3. PostgreSQL operation scheduler/advisory lock с durable queue и fencing version; Egress write разрешать только владельцу актуального fence.

Предпочтителен вариант 3 для correctness: состояние jobs уже хранится в PostgreSQL, lock и переходы переживают restart. Redis можно использовать как ускоряющий admission layer, но не как единственный источник ownership.

### [A-04] Notification/Audit — heartbeat-заглушки

Варианты:

1. Удалить их из production manifest до реализации.
2. Оставить healthy placeholders с явным статусом `NotReady/NotImplemented`.
3. Реализовать минимальные consumers и durable audit/notification delivery перед включением.

Предпочтителен вариант 1 как немедленный честный rollout, затем вариант 3: неработающий компонент не должен создавать green health и эксплуатационные ожидания. Вариант 2 полезен только в test environment.

## Security

### [S-01] Deactivation может оставить действующую session

Варианты:

1. Повторять Redis revoke синхронно до успеха. Увеличивает callback latency и всё равно не гарантирует завершение после process crash.
2. На каждом Gateway request синхронно проверять installation в Vendor/DB. Корректно, но добавляет latency и жёсткую runtime dependency.
3. Записать durable revocation event/version в одной DB-транзакции, доставить через outbox и проверять version в session/Gateway с коротким cache.

Предпочтителен вариант 3: он переживает crash и не требует remote lookup на каждый запрос. До его внедрения вариант 2 можно использовать как fail-closed временную защиту для destructive endpoints.

### [S-02] Нет явной CSRF-защиты

Варианты:

1. Переключить cookie на `SameSite=Lax/Strict`. Просто, но может сломать iframe integration.
2. Проверять `Origin`, `Sec-Fetch-Site` и разрешённый iframe origin.
3. Использовать anti-forgery token, привязанный к session, совместно с строгим Origin/Fetch Metadata.

Предпочтителен вариант 3: iframe требует `SameSite=None`, поэтому полагаться на cookie policy нельзя. Token защищает state-changing запросы, а Origin/Fetch Metadata дают дополнительный дешёвый барьер.

### [S-03] Access token идёт по plaintext HTTP

Варианты:

1. Полагаться на private Docker network. Нулевая стоимость, но не защищает от compromised container/network namespace.
2. Включить HTTPS/mTLS между Vendor и Egress, оставив token response contract.
3. Перенести token-bound HTTP execution в Vendor-controlled proxy/sidecar, чтобы Egress не получал raw token.

Предпочтителен вариант 2 как практический следующий шаг: он минимально меняет boundary и даёт mutual identity/confidentiality. Вариант 3 сильнее по least privilege, но требует более крупного redesign и оправдан при повышенной модели угроз.

### [S-04] Общий Compose env нарушает least privilege

Варианты:

1. Разделить YAML anchors по типам зависимостей.
2. Использовать service-specific env files/secrets с одним общим credential на ресурс.
3. Выдать отдельные DB/Kafka/Redis/service identities с минимальными ACL и доставлять их через secret manager.

Предпочтителен вариант 3: простое разделение env уменьшает случайное раскрытие, но общий credential всё ещё даёт lateral movement. Уникальные identities также обеспечивают аудит и независимую ротацию.

### [S-05] Preview отдаёт RawJson frontend

Варианты:

1. Удалить поле из DTO сразу. Самый безопасный путь, но может быть breaking для скрытого consumer.
2. Добавить новый versioned DTO/endpoint с allowlist и постепенно переключить frontend.
3. Оставить RawJson, применяя redaction известных полей. Хрупко: новые upstream поля снова утекут.

Предпочтителен вариант 2: explicit allowlist безопасен при изменении схемы МойСклада и позволяет контролируемую миграцию. Если endpoint гарантированно внутренний и consumer один, вариант 1 быстрее.

### [S-06] Swagger/OpenAPI доступны в production

Варианты:

1. Environment guard полностью отключает UI/routes в Production.
2. Оставить OpenAPI, защитив admin auth/VPN.
3. Публиковать sanitized external API spec отдельно, internal specs хранить как CI artifacts.

Предпочтителен вариант 3: frontend/партнёры сохраняют документированный публичный контракт, а internal surface не раскрывается runtime. Вариант 1 — немедленный quick fix.

## Kafka

### [K-01] Sync возвращает 202 без durable operation/outbox

Варианты:

1. Возвращать 202 только после Kafka acknowledgement. Это уже почти текущее поведение и не создаёт queryable durable operation.
2. Создавать `sync_run=pending`, затем напрямую publish; при ошибке отмечать failed. Остаётся окно между DB commit и Kafka.
3. В одной DB-транзакции создавать run и command outbox, возвращать 202 после commit; publisher доставляет Kafka, HTTP idempotency key возвращает тот же run.

Предпочтителен вариант 3: он делает `202` правдивым даже при недоступной Kafka и закрывает ambiguous response/retry. Существующий outbox pattern позволяет внедрить его последовательно.

### [K-02] Длительные handlers не настраивают max.poll.interval.ms

Варианты:

1. Просто увеличить `MaxPollIntervalMs` выше худшего sync. Быстро, но верхняя граница может расти и замедлит recovery dead consumer.
2. Делить command на короткие Kafka messages/pages. Сохраняет polling, но создаёт много orchestration messages.
3. Consumer быстро claim-ит durable job, а отдельный bounded worker выполняет шаги; consumer продолжает poll, ownership защищён lease/fence.

Предпочтителен вариант 3: длительная business operation отделяется от Kafka group liveness, а прогресс остаётся в PostgreSQL. Увеличение interval стоит применить дополнительно как safety margin, а не как основную гарантию.

### [K-03] Merge retry использует фиксированный loop

Варианты:

1. Exponential delay внутри consumer loop. Лучше 2 секунд, но блокирует partition и может нарушить max poll.
2. Retry topic с delayed consumption/DLQ.
3. Durable `next_attempt_at` в operation/job и scheduler, который публикует/исполняет готовые attempts; учитывает Redis `blocked_until`, Retry-After и jitter.

Предпочтителен вариант 3: attempts уже являются DB-state, поэтому backoff становится наблюдаемым, перезапускаемым и не удерживает Kafka partition. Retry topic может служить transport, но не источником состояния.

### [K-04] Invalid SyncRequested коммитится без DLQ

Варианты:

1. Не commit-ить invalid message. Это создаёт бесконечный poison loop.
2. Сохранять incident в PostgreSQL и commit source.
3. Публиковать sanitized DLQ envelope, дождаться acknowledgement, затем commit; при недоступном DLQ не commit-ить.

Предпочтителен вариант 3: сохраняется payload hash/позиция для анализа и не блокируется основной partition. DB incident можно добавить для UI/retention, но Kafka DLQ естественнее для replay pipeline.

### [K-05] Outbox rows не claim-ятся

Варианты:

1. Разрешить только одну replica publisher. Просто, но создаёт singleton bottleneck/SPOF.
2. `SELECT ... FOR UPDATE SKIP LOCKED` в короткой транзакции.
3. Atomic claim с `owner_id/locked_until`, публикация вне DB transaction и повторный claim после lease expiry.

Предпочтителен вариант 3: нельзя держать DB transaction во время Kafka network call, а lease переживает crash. Дубликаты всё равно возможны после publish-before-mark, поэтому inbox остаётся обязательным.

### [K-06] Sync contracts без schema version

Варианты:

1. Добавить integer `schemaVersion` в текущие records с default.
2. Ввести versioned envelope (`type`, `version`, metadata, payload`) и compatibility tests.
3. Подключить Schema Registry/Avro/Protobuf.

Предпочтителен вариант 2 сейчас: он решает explicit routing без немедленного инфраструктурного усложнения. Вариант 3 оправдан при множестве независимых producers/consumers и строгом governance.

## Database

### [D-01] Full sync удаляет каталог до snapshot

Варианты:

1. Загружать всё в память, затем delete+insert одной транзакцией. Сохраняет старый каталог до загрузки, но транзакция/память велики.
2. Upsert pages сразу и после успеха удалить unseen rows. Частичный sync виден читателям без versioning.
3. Staging/versioned snapshot: писать pages с `snapshot_id`, валидировать counts, атомарно переключить active version, чистить старую позже.

Предпочтителен вариант 3: минимальная блокировка, bounded processing и last-known-good остаётся доступным при любой ошибке. Это также основа для safe reconciliation.

### [D-02] Нет optimistic concurrency

Варианты:

1. Использовать PostgreSQL `xmin` как EF concurrency token.
2. Добавить явную `version bigint` каждой изменяемой aggregate row.
3. Полагаться только на account-level distributed lock.

Предпочтителен вариант 2: явная версия стабильна для API/events/fencing и не привязана к деталям PostgreSQL MVCC. Lock полезен для sequencing, но не обнаруживает stale writer после lease expiry.

### [D-03] Merge POST не идемпотентен и допускает конфликтующие jobs

Варианты:

1. Блокировать кнопку frontend. Не защищает network retry/другого пользователя.
2. Idempotency key с unique account constraint, но без resource reservation.
3. Idempotency key плюс active reservation rows/unique partial constraint на каждый counterparty; освобождение только terminal job.

Предпочтителен вариант 3: idempotency закрывает повтор того же запроса, reservation — разные конфликтующие запросы. Это две отдельные угрозы, и одной frontend блокировки недостаточно.

### [D-04] Counterparty PK не включает account_id

Варианты:

1. Оставить как есть после документального подтверждения глобальной UUID uniqueness.
2. Добавить surrogate internal PK, unique `(account_id,moysklad_id)`.
3. Сделать composite PK `(account_id,id)` и обновить FKs.

Предпочтителен вариант 1, если официальная/эмпирическая гарантия подтверждена: миграция не нужна. Если нет — вариант 2 обычно проще для EF relationships и позволяет безопасно tenant-scope external ID; решение нельзя принимать без проверки.

### [D-05] Нет retention/cleanup

Варианты:

1. Периодический DELETE batches по timestamp/status.
2. PostgreSQL partitioning по времени с detach/drop partitions.
3. Перенос старых records/raw payload в object storage, затем DB cleanup.

Предпочтителен вариант 1 для текущего масштаба как простой управляемый baseline; при доказанном большом объёме перейти к 2, а 3 применять только если audit/compliance требует долгого хранения payload.

### [D-06] Два сервиса мигрируют одну схему

Варианты:

1. Оставить startup migrations, надеясь на advisory lock EF/provider.
2. Назначить один application service migration owner.
3. Отдельный CI/CD migration job с version check; application containers только проверяют совместимость.

Предпочтителен вариант 3: schema change отделён от replica startup, наблюдаем и откатывается по deployment procedure. Вариант 2 всё ещё связывает availability приложения с DDL.

### [D-07] Unknown deactivation не сохраняет idempotency

Варианты:

1. Считать повтор harmless и ничего не хранить.
2. Создать generic callback inbox с request ID/payload hash/result.
3. Создавать synthetic installation tombstone.

Предпочтителен вариант 2: он решает idempotency без появления фиктивной domain entity и одинаково работает для всех callback outcomes. Tombstone уместен только если нужен долгий lifecycle audit аккаунта.

## Redis

### [R-01] Distributed rate limiter отсутствует

Варианты:

1. In-memory semaphore/token bucket в каждом Egress instance. Просто, но aggregate limit обходится масштабированием.
2. Redis Lua token bucket по account/user/global scope.
3. Redis-based scheduler с buckets, concurrency permits, `blocked_until`, priority queues и fencing; merge writes имеют более высокий приоритет.

Предпочтителен вариант 3, потому что архитектурное требование включает не только rate, но и priority/fairness между full sync и merge. Вариант 2 можно поставить первым этапом, если приоритетная очередь внедряется отдельно.

### [R-02] Gateway activity не продлевает session

Варианты:

1. Frontend периодически вызывает `/me`. Просто, но создаёт keepalive traffic и зависит от вкладки.
2. Gateway обновляет TTL на каждый request. Корректно, но создаёт Redis write amplification.
3. Gateway touch-ит session только если остаток TTL/last activity меньше порога, используя atomic script; cookie переиздаётся с тем же throttling.

Предпочтителен вариант 3: sliding semantics соответствуют реальной активности без записи на каждый запрос. Frontend keepalive не должен быть security primitive.

### [R-03] Revoke конкурирует с Create

Варианты:

1. Lua script атомарно удаляет текущий set и keys. Не предотвращает Create сразу после script.
2. Account distributed lock вокруг Create/Revoke.
3. Revocation generation/status: Create читает active generation, session содержит её, Gateway отклоняет устаревшую; Lua/fencing сериализует transitions.

Предпочтителен вариант 3: correctness не зависит от вечной жизни lock и stale session становится недействительной даже если key физически остался. Lock можно использовать как оптимизацию.

## HTTP / Egress

### [H-01] Vendor token timeout не маппится

Варианты:

1. Общий exception middleware преобразует все cancellations в 503. Риск ошибочно превратить caller cancellation.
2. В `VendorTokenClient` отдельно catch timeout при `!callerToken.IsCancellationRequested`.
3. Standard resilience handler с timeout/error mapping для всех internal clients.

Предпочтителен вариант 2 немедленно: точная семантика и минимальный риск. Вариант 3 полезен позже для унификации, но policy должна различать timeout и caller cancellation.

### [H-02] Sync теряет correlation ID

Варианты:

1. Передавать только `X-Correlation-Id` вручную.
2. Добавить correlation field в sync contracts/DB.
3. Применить W3C `traceparent` + baggage/correlation, записать trace ID в operation и Kafka headers.

Предпочтителен вариант 3: он совместим с OpenTelemetry и связывает spans, а не только log string. Business correlation ID можно оставить отдельно для поддержки/API.

### [H-03] Correlation header без лимита

Варианты:

1. Ограничить строку, например 128 ASCII chars.
2. Требовать GUID.
3. Принимать валидный W3C traceparent/UUID, иначе генерировать server value; никогда не отражать raw invalid input.

Предпочтителен вариант 3: поддерживает стандартную трассировку, остаётся fail-safe и исключает high-cardinality произвольные значения.

## MoySklad

### [M-01] Archive batch больше 1000

Варианты:

1. Reject merge groups >1000. Просто, но пользователь не может завершить легитимный merge.
2. Делить на chunks ≤1000 и сохранять chunk progress.
3. Выполнять отдельный request на duplicate. Максимальная точность, но много calls и rate pressure.

Предпочтителен вариант 2 с размером ниже hard limit и per-item operations: соответствует API, использует batching и позволяет продолжить с последнего подтверждённого chunk. Вариант 1 годится как временный guard.

### [M-02] Partial failure применяется ко всему batch

Варианты:

1. При любой неоднозначности GET каждого duplicate и reconcile state.
2. Парсить per-position success/error из batch и reconcile только ambiguous items.
3. Отказаться от batch и архивировать по одному.

Предпочтителен вариант 2: сохраняет эффективность batch и минимизирует дополнительные GET, одновременно давая корректное состояние каждой operation. Вариант 1 должен быть fallback для timeout/malformed response.

### [M-03] Rate-limit headers учитываются неполно

Варианты:

1. Добавить недостающий header только в logs.
2. Использовать headers локально для delay текущего instance.
3. Нормализовать официальные headers и атомарно обновлять Redis rate state/`blocked_until`, который читают все instances.

Предпочтителен вариант 3: upstream limit общий относительно нескольких workers, поэтому локальный delay не обеспечивает соблюдение aggregate budget.

### [M-04] Discovery URL не ограничен

Варианты:

1. Ввести консервативный maximum IDs и вернуть controlled 400.
2. Вычислять encoded URI length и reject до отправки.
3. Chunk agent filters, агрегировать результаты и deduplicate с полной count validation.

Предпочтителен вариант 3, если discovery обязан поддерживать большие selections; иначе вариант 2 проще и точнее фиксированного числа, потому что реальный предел зависит от encoded length. Chunking требует учесть документ, совпавший с несколькими входными agents.

## Docker / infrastructure

### [DC-01] Kafka без persistence/redundancy

Варианты:

1. Подключить существующий volume к одному broker. Защищает от container recreation, но не от host/disk failure.
2. Три broker/controller nodes с persistent volumes и replication factor 3/min ISR 2.
3. Managed Kafka с SLA/backups/ACL.

Предпочтителен вариант 2 для self-hosted production: он устраняет подтверждённый SPOF без зависимости от нового provider. Вариант 3 предпочтительнее при отсутствии команды эксплуатации Kafka; решение зависит от организационной способности, а не только кода.

### [DC-02] Build context содержит secrets

Варианты:

1. Добавить `.dockerignore` для `.env*`, `.git`, tests/artifacts.
2. Использовать узкий per-project context.
3. Переписать Dockerfile на selective COPY project files/source и BuildKit secret mounts для неизбежных build credentials.

Предпочтителен вариант 3 вместе с `.dockerignore`: selective allowlist безопаснее denylist, потому что новый секретный файл не попадёт в context автоматически. Сначала всё равно нужна rotation `C-01`.

### [DC-03] Admin UI входят в production Compose

Варианты:

1. Добавить всем UI `dev-tools` profile и localhost bind.
2. Оставить в production, закрыв basic/OIDC auth.
3. Полностью вынести operational UI в отдельный admin environment/VPN с audit access.

Предпочтителен вариант 1 как немедленное исправление текущего Compose, затем 3 для production operations. Auth без network isolation оставляет лишнюю public surface.

### [DC-04] Root containers и Vite preview

Варианты:

1. Только добавить non-root `USER` в текущие images.
2. Multi-stage frontend build + nginx/Caddy non-root; .NET runtime dedicated UID.
3. Публиковать frontend assets через CDN/object storage, backend остаётся non-root container.

Предпочтителен вариант 2 как переносимое решение для текущего Compose. CDN лучше по scale/cache, но добавляет infrastructure и не нужен для устранения root/Vite risk.

### [DC-05] Health/readiness не отражают dependencies

Варианты:

1. Переключить Compose healthcheck с `/live` на `/ready`.
2. Разделить startup/liveness/readiness и добавить dependency checks всем сервисам.
3. Добавить ещё business health: consumer assigned/running, outbox lag, last successful publish.

Предпочтителен вариант 2 как базовая корректность; вариант 3 нужен для alerts, но не всегда должен блокировать traffic, иначе transient Kafka outage вызовет restart cascade.

### [DC-06] Host/internal ports смешаны

Варианты:

1. Зафиксировать container ports 5432/6379, переменными управлять только host side.
2. Разделить `*_HOST_PORT` и `*_INTERNAL_PORT` и реально конфигурировать daemons.
3. Убрать production host mappings полностью, оставив только DNS/service ports.

Предпочтителен вариант 1 для dev и 3 для production: internal defaults стабильны, а внешняя публикация stateful services там не требуется. Вариант 2 добавляет настройку без полезной необходимости.

## Observability

### [O-01] Логируются полные archive bodies

Варианты:

1. Удалить body logs полностью.
2. Redact known fields и truncation.
3. Логировать allowlisted metadata (`count`, IDs операций, status, duration), а диагностический payload хранить только как access-controlled short-lived trace artifact по feature flag.

Предпочтителен вариант 3: штатные logs остаются безопасными и полезными, а редкие диагностики возможны под явным контролем. Redaction blacklist хрупок к новым upstream fields.

### [O-02] Нет end-to-end sync trace

Варианты:

1. Добавить один correlation ID во все log messages.
2. Передавать correlation через HTTP/Kafka и DB.
3. Автоматическая OpenTelemetry instrumentation + W3C context, дополненная business IDs (`sync_run_id`, `account_id`).

Предпочтителен вариант 3: trace показывает causal graph/latency, а business IDs позволяют искать операцию после окончания trace retention. Только ручные log fields легко пропустить на новом hop.

### [O-03] Нет метрик/tracing

Варианты:

1. Добавить Prometheus counters вручную.
2. OpenTelemetry SDK + OTLP exporter и стандартные ASP.NET/HttpClient/runtime metrics.
3. Полный vendor APM agent без code changes.

Предпочтителен вариант 2: vendor-neutral, контролируемая cardinality и возможность связать custom job/rate/outbox metrics со spans. APM agent быстрее, но business state всё равно потребует instrumentation.

### [O-04] Workers логируют heartbeat каждую секунду

Варианты:

1. Увеличить interval/log level.
2. Удалить heartbeat logs и использовать gauge/health.
3. Удалить placeholder services из production (`A-04`).

Предпочтителен вариант 3 до реализации сервисов; после реализации — вариант 2. Периодическое сообщение не является проверкой здоровья независимо от частоты.

### [O-05] Непоследовательные startup/error logs

Варианты:

1. Заменить `Console.WriteLine` на ILogger.
2. Ввести shared logging conventions/event IDs и severity table.
3. Добавить middleware enrichment/redaction и structured logging analyzer/tests.

Предпочтителен вариант 2 как небольшой, но системный шаг, затем 3 для enforcement. Простая замена Console не исправляет alert noise от expected 4xx.

## Tests

### [T-01] Нет real-infrastructure tests critical flows

Варианты:

1. Расширять только unit/fake tests. Быстро, но не ловит broker/DB semantics.
2. Testcontainers PostgreSQL/Redis/Kafka для component integration tests.
3. Дополнить вариант 2 небольшим nightly end-to-end environment с fault injection.

Предпочтителен вариант 2 как обязательный PR gate и вариант 3 для дорогих crash/rebalance scenarios. Так feedback остаётся быстрым, а distributed failures всё же проверяются.

### [T-02] Падает cookie-policy test

Варианты:

1. Изменить test под текущий `Secure=None`.
2. Изменить controller под expected environment-aware policy.
3. Сначала зафиксировать supported iframe origins/HTTPS contract в security decision, затем table-driven tests для dev/prod/forwarded HTTPS.

Предпочтителен вариант 3: без browser/deployment контракта нельзя определить, ошибочен код или test. Подгонка одной стороны скрыла бы реальную security/compatibility проблему.

### [T-03] Redis test молча проходит

Варианты:

1. Явно `Skip` при отсутствии env.
2. Fail test, если env отсутствует.
3. Поднимать Redis Testcontainer внутри integration fixture; category можно явно отключить только отдельным pipeline profile.

Предпочтителен вариант 3: тест самодостаточен и одинаков локально/CI. Явный Skip лучше текущего поведения, но всё ещё позволяет никогда не запускать проверку.

### [T-04] Partial failure test закрепляет неверное поведение

Варианты:

1. Переименовать test как all-batch failure, не меняя assertions.
2. Добавить отдельные mixed success/error cases.
3. Создать matrix: per-item error, malformed response, timeout-after-partial-apply, retry/reconciliation; текущий test оставить только для total failure.

Предпочтителен вариант 3: partial failure — state machine, и один happy/error test не покрывает ambiguous outcomes. Переименование нужно, но не заменяет coverage.

## Performance

### [P-01] Preview загружает весь tenant catalog

Варианты:

1. Добавить hard cap и вернуть first N groups.
2. Выполнять `GROUP BY/HAVING count>1` в PostgreSQL и page results.
3. Предвычислять duplicate index/materialized groups при sync.

Предпочтителен вариант 2: использует существующие normalized columns/indexes и сохраняет актуальность без нового consistency pipeline. Вариант 3 нужен только при доказанно высокой частоте preview/очень больших tenants.

### [P-02] Full sync буферизует snapshot

Варианты:

1. Увеличить memory limits. Не устраняет линейный рост.
2. Stream pages прямо в текущую таблицу. Рискует показать partial state.
3. Stream/bulk-copy pages в staging snapshot, валидировать и атомарно активировать (`D-01`).

Предпочтителен вариант 3: одновременно исправляет memory и correctness. Простая потоковая запись в live table меняет одну проблему на partial visibility.

### [P-03] SaveChanges на каждую operation

Варианты:

1. Один `SaveChanges` после изменения всех tracked operations.
2. `ExecuteUpdate` batch transition по job/status.
3. Bulk extension/library.

Предпочтителен вариант 1 для текущего размера модели: минимальное усложнение и одна транзакция. `ExecuteUpdate` полезен при очень больших batches, но требует аккуратно синхронизировать change tracker.

### [P-04] RawJson дублирует payload

Варианты:

1. Полностью отказаться от RawJson.
2. Хранить только allowlisted extracted fields + upstream hash/version.
3. Вынести compressed raw в cold object storage с retention/reference.

Предпочтителен вариант 2, если raw нужен только для change/reconciliation detection; вариант 3 — если есть доказанное audit требование полного ответа. Полное удаление без выяснения use cases может потерять forensic value.

## Code quality

### [Q-01] Крупные workflow-классы и magic statuses

Варианты:

1. Механически разбить классы на меньшие partial/services.
2. Сначала формализовать state transitions и value types, затем выделить pure validators, persistence и gateways.
3. Переписать на workflow framework/state-machine library.

Предпочтителен вариант 2: декомпозиция следует invariants и улучшает тестируемость без крупной framework migration. Простое разбиение по размеру может только размазать связанность.

### [Q-02] Неполная нормализация телефона

Варианты:

1. Удалять все non-digits и применять правило `8→7`.
2. Использовать библиотеку libphonenumber с configured default region и E.164.
3. Сопоставлять несколько derived keys без изменения сохранённого canonical value.

Предпочтителен вариант 2, если accounts могут содержать международные номера: стандартизированная parsing/validation меньше создаёт false matches. Для строго российского продукта вариант 1 дешевле, но правило должно быть явно зафиксировано тестами.

### [Q-03] Makefile и README расходятся

Варианты:

1. Вручную синхронизировать names/commands.
2. Сделать Makefile единственным executable source, а README ссылаться на `make help`.
3. Заменить Makefile task runner/script-ами с CI smoke test.

Предпочтителен вариант 2: малая стоимость, меньше дублирования и автоматический discoverability. Новый task runner не оправдан только этой проблемой.

### [Q-04] Tracked Python bytecode

Варианты:

1. Удалить `.pyc` и добавить ignore.
2. Оставить как воспроизводимый artifact. `.pyc` не является переносимым/reproducible source artifact.
3. Хранить versioned packaged tool отдельно.

Предпочтителен вариант 1: для test-data script достаточно `.py`; это стандартная минимальная мера без потери функциональности.

## Environment

### [E-01] Неиспользуемые/общие env variables

Варианты:

1. Удалить только явно неиспользуемые variables.
2. Ввести strongly typed options + `ValidateOnStart` и service-specific env blocks.
3. Генерировать manifests/config schema из deployment tooling.

Предпочтителен вариант 2: он не только очищает текущий noise, но и предотвращает silent typos/stale mappings. Генерация оправдана позже при большом числе environments.

### [E-02] Weak DB fallback в appsettings

Варианты:

1. Оставить defaults только в `appsettings.Development.json`.
2. Удалить password fallback и fail-fast вне Development.
3. Использовать local user-secrets/dev container injection и mandatory production secret reference.

Предпочтителен вариант 3: developer convenience сохраняется без committed credential, а production не может молча стартовать со слабым default. Вариант 2 является минимальной обязательной частью.

## Общий принцип выбора

Для P0/P1 рекомендуется не выбирать «самый маленький diff», если он оставляет тот же класс отказа. Предпочтительные варианты дают durable source of truth, fail-closed security и корректность при нескольких replicas. Упрощённые варианты отмечены как временные guards там, где они быстро уменьшают риск и не мешают целевой миграции.
