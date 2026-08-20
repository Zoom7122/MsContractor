# Critical findings

## [C-01] Действующие секреты хранятся в Git

Severity: Critical

Category: Security

Location: `.env.dev`; `.env.prod`; `scripts_for_test_data/.env.test_data`

Lines: `.env.dev:3,26,30-31`; `.env.prod:3,25,29-30`; `.env.test_data:3`

Current behavior: Файлы отслеживаются Git и содержат непустые PostgreSQL password, internal API key, Vendor secret, token encryption key и пароль тестовой учётной записи. История этих файлов существует. Общие чувствительные значения dev/prod совпадают.

Problem: Секреты доступны каждому, кто получил repository/history; удаление только из текущего commit не отзывает уже раскрытые значения.

Risk: Компрометация БД, internal API, Vendor callback/JWT, расшифровка сохранённых access token и аккаунта МойСклад.

Example scenario: Утёкший `INTERNAL_API_KEY` используется вместе с опубликованным Egress port для запроса token-backed операций произвольного аккаунта.

Recommended solution: Считать значения скомпрометированными, отозвать/ротировать их, перенести в secret manager/CI secrets, оставить только шаблоны, затем очистить Git history и проверить downstream logs/caches/images.

Priority: P0

Confidence: High

## [C-02] Production Compose принудительно включает Development и dev-session

Severity: Critical

Category: Security

Location: `docker-compose.yml`; `src/Services/VendorService/Controllers/MoyskladSessionController.cs`; `.env.prod`

Lines: `docker-compose.yml:3-5,165-172`; controller `75-94`; `.env.prod` — `DEV_ACCOUNT_ID` отсутствует

Current behavior: Общий Compose env всегда задаёт `ASPNETCORE_ENVIRONMENT=Development`. Gateway проксирует `/api/moysklad/session/**`, а `POST .../dev` создаёт сессию для configured account без пользовательской аутентификации. Одновременно `.env.prod` не задаёт обязательный `DEV_ACCOUNT_ID`, поэтому production Compose либо не стартует, либо требует вручную включить этот dev account.

Problem: Production profile фактически отсутствует; безопасностная граница dev endpoint определяется заведомо неверным environment.

Risk: Account impersonation и доступ к данным выбранного аккаунта через публичный Gateway.

Example scenario: Оператор добавляет `DEV_ACCOUNT_ID`, чтобы Compose с `.env.prod` стартовал; любой внешний клиент вызывает dev endpoint и получает session cookie.

Recommended solution: Создать отдельный production override/profile с `Production`, полностью исключить dev endpoint/option из production DI/routes и добавить deployment test, что маршрут возвращает 404.

Priority: P0

Confidence: High

## [C-03] Internal tenant API опубликованы наружу и допускают выбор accountId

Severity: Critical

Category: Security

Location: `docker-compose.yml`; `src/Services/MoySkladEgressService/Controllers/InternalCounterpartiesController.cs`; `src/Services/CatalogSyncService/Controllers/InternalSyncController.cs`; `src/Services/DuplicatesMergeService/Controllers/InternalMergeJobsController.cs`

Lines: Compose `187-197,213-223,241-251`; route/controller validation — соответствующие `internal/**` actions

Current behavior: Egress, CatalogSync и DuplicatesMerge публикуют ports на все host interfaces. Internal endpoints доверяют route/body/header `accountId` после проверки одного статического `X-Internal-Api-Key`; ключ уже находится в Git (`C-01`).

Problem: Сетевой perimeter и tenant identity сводятся к одному shared secret. Egress получает Vendor token по переданному account и выполняет операции в МойСкладе от имени этого аккаунта.

Risk: Межаккаунтное чтение/изменение данных (Critical по правилам аудита), запуск sync/merge и архивирование чужих контрагентов.

Example scenario: Клиент с утёкшим ключом вызывает host-published `PUT /internal/accounts/{victimAccount}/counterparties/archive` с выбранными UUID.

Recommended solution: Не публиковать internal ports, сегментировать сеть, перейти на workload identity/mTLS и формировать tenant context из проверенной service identity; провести отдельный authorization test на каждый internal action.

Priority: P0

Confidence: High

## [C-04] PostgreSQL, Redis, Kafka и admin UI доступны с host без production auth/TLS

Severity: Critical

Category: Docker

Location: `docker-compose.yml`

Lines: `21-30,41-46,57-68,71-85,107-120`

Current behavior: PostgreSQL, Redis, Kafka, RedisInsight и Kafka UI имеют host port mappings без localhost binding. Redis/Kafka/UI не настроены на auth/TLS; DB credentials скомпрометированы через Git.

Problem: Stateful tenant data и session store доступны в обход Gateway/service authorization.

Risk: Чтение/изменение данных любых аккаунтов, создание/подмена Redis sessions, публикация Kafka commands, удаление состояния.

Example scenario: Доступный извне Redis позволяет читать `vendor:session:*` и изменять account/employee JSON либо удалить rate/session state.

Recommended solution: Немедленно закрыть security-group/firewall и host publishing, отделить production infrastructure, включить auth/TLS/ACL, ротировать credentials и проверить журналы доступа.

Priority: P0

Confidence: High
