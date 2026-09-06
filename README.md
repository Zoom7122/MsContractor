# MsContractor

Backend на .NET 10 состоит из семи сервисов в `src/Services`. Общие HTTP/Kafka-контракты находятся в `src/Shared/MsContractor.Contracts`, общая инфраструктура — в `MsContractor.BuildingBlocks`. Frontend — `src/frontend-Iframe`.

## Слои backend

| Папка | Назначение |
| --- | --- |
| `Controllers` | HTTP-маршруты, авторизация, входные параметры, HTTP-ответы. |
| `Consumers` | Получение Kafka-сообщений, десериализация, запуск сервисов, offsets и транспортные повторы. |
| `Services` | Бизнес-правила и последовательность выполнения операций. |
| `Repositories` | Запросы и сохранение PostgreSQL/Redis, атомарные операции хранения. |
| `Persistence` | DbContext, tenant interceptors, EF migrations и snapshots в `Migrations`. |
| `Clients` | HTTP-клиенты внутренних сервисов и MoySklad Vendor API. |
| `Gateways` | Адаптеры MoySklad JSON API, обработка и валидация HTTP-ответов, текущий rate-limit hook. |
| `Messaging` | Kafka publishers, outbox publishers и публикация DLQ. |
| `Contracts` | Локальные транспортные DTO; общие межсервисные DTO остаются в Shared. |
| `Models` | Сущности и внутренние типы; настройки — в `Options`, общие для слоёв исключения — в `Exceptions`. |
| `Middleware` | Обработка HTTP-запросов в middleware pipeline. |
| `HealthChecks` | Реализации проверок зависимостей. |

Namespace соответствует папке. Интерфейсы расположены рядом с реализациями своего слоя. `Program.cs`, конфигурация и файлы проектов остаются в корне сервиса. Пустые слои не создаются.

- **Gateway.Bff**: HTTP-клиенты в `Clients`, проверка сессий в `Services`, Redis-чтение в `Repositories`.
- **VendorService**: установки и сессии обрабатывают сервисы; PostgreSQL/Redis доступны через репозитории. Vendor API вызывает клиент контекста.
- **CatalogSyncService**: consumer запускает `SyncProcessor`; репозиторий сохраняет снимок, sync run, watermark, inbox и outbox. При full sync сначала удаляются документы текущего аккаунта, затем контрагенты; загрузка нового снимка начинается после очистки.
- **DuplicatesMergeService**: preview и merge остаются в сервисах; репозитории отвечают за контрагентов, jobs/operations, снимки документов и outbox. Порядок обработки: discovery → обновление основного контрагента → перенос документов → архивирование дублей.
- **MoySkladEgressService**: сценарии discovery/change и пересоздания salesreturn находятся в `Services`, обращения к MoySklad JSON API и обработка ответов — в `Gateways`, запрос токена Vendor — в `Clients`. Журнал пересоздания хранится в PostgreSQL (`egress`); readiness проверяет PostgreSQL и Redis. Rate limiter пока только наблюдает заголовки, его ожидание остаётся no-op.
- **AuditService / NotificationService**: существующие фоновые заглушки `Worker` находятся в `Services`; consumers и хранилища для них пока не реализованы.

Merge использует `CatalogSyncDbContext` и сущности из CatalogSync через собственные репозитории. Контекст и миграции остаются у CatalogSync. Репозитории одной операции получают один scoped DbContext; транзакции и EF-запросы не выходят в бизнес-сервисы. Бизнес-правило применения incremental-строки передаётся репозиторию как функция, чтобы чтение и сохранение оставались в одной транзакции. Существующие глобальные inbox-ключи и межаккаунтная выборка outbox сохранены.

## Сборка и проверки

```bash
dotnet build MsContractor.sln -c Release
dotnet test MsContractor.sln -c Release
```

Тесты включают проверку границ слоёв, migration IDs и snapshots, rollback репозиториев, tenant isolation и общую область жизни DbContext. Redis-тесты выполняют обращения к Redis, когда задана `TEST_REDIS_CONNECTION`; без неё соответствующие тесты завершаются без интеграционных проверок. Для них следует использовать отдельный тестовый Redis.

До рефакторинга выявлено падение Vendor-теста `CreateAsync_IssuesEnvironmentAppropriateHttpOnlyCookie` для Development без HTTPS: тест ожидает `SameSite=Lax`. Поведение cookie в рамках изменения структуры сохранено.

## Локальный запуск

```bash
make compose-up
docker compose --env-file .env.dev --profile dev-tools ps
make logs
make health
make compose-down
```

`make compose-up` собирает и запускает dev-стек. `make health` проверяет HTTP readiness и инфраструктуру. Dozzle доступен локально на `http://localhost:8089`, входит в профиль `dev-tools`; текущая Compose-конфигурация предназначена для разработки.

## Пересоздание salesreturn через Egress

`POST /internal/accounts/{accountId}/documents/salesreturn/recreate` принимает `mainCounterpartyId` и массив `documents`. Каждый элемент содержит `oldDocumentId`, `duplicateCounterpartyId`, необязательные `newAgentAccountId` / `newContractId` и `data` с полным содержимым для копирования. Пример запроса находится в `src/Services/MoySkladEgressService/MsContractor.MoySkladEgressService.http`.

Обязательны `X-Internal-Api-Key`, `X-User-Id`, `X-Merge-Job-Id`, `X-Merge-Operation-Id`; `X-Correlation-Id` можно передать дополнительно. При повторе сохраняйте operation ID, job/user IDs и содержимое запроса. Изменённый запрос с тем же operation ID возвращает `409`.

Egress проверяет принадлежность оригинала дубликату, нового счёта/договора — главному КА. Если передан `demand`, его КА уже должен быть главным: отгрузку этот endpoint не переносит. `data.positions` должен быть полным непустым массивом позиций; metadata страницы позиций не заменяет массив. Используется содержимое из запроса, а не автоматически загруженные позиции оригинала.

Копируются `name`, `moment`, `applicable`, `description`, `code`, `externalCode`, `organization`, `organizationAccount`, `store`, `demand`, `project`, `state`, `salesChannel`, `rate`, `vatEnabled`, `vatIncluded`, `attributes`, `positions`, `owner`, `group`, `shared`. `agent` заменяется главным КА; `agentAccount` и `contract` строятся из новых ID либо исключаются вместе со старыми значениями. Служебная идентичность оригинала и позиций не переносится. В создание добавляется новый технический `syncId`.

Порядок: сохранить payload и syncId → проверить документы → завершить проход удаления всех подходящих оригиналов → создать замены пакетами до 1000. Ошибка проверки/удаления одного документа не препятствует обработке остальных. Создание выполняется только для подтверждённо удалённых оригиналов. Частичные ответы сопоставляются по syncId; неатрибутированные ошибки повторяются отдельными пакетами из одного документа.

Ответ `200` означает, что все элементы имеют `status=Completed`. Ответ `207` содержит частичные результаты: `oldDocumentId`, `newDocumentId`, `stage`, `status` (`Completed`, `Pending`, `Failed`), `errorCode`, `error`, `retryable`. Если создание отклонено после удаления, оригинал автоматически не восстанавливается: payload остаётся в журнале. Полная транзакция между удалением и созданием в МойСклад отсутствует.

`EgressDbContext` владеет таблицами `egress.salesreturn_operations` и `egress.salesreturn_claims`; миграции выполняются при старте. PostgreSQL обязателен, используется `ConnectionStrings:Postgres`. Составной ключ аккаунт/операция, уникальные claims аккаунт/старый документ и PostgreSQL advisory lock на аккаунт защищают повторы и пересечения. Claims сохраняются вместе с журналом; другой operation ID не может повторно захватить те же оригиналы. Уже завершённая операция возвращает сохранённый результат.

Фоновый `SalesReturnRecoveryWorker` проверяет журнал каждые 10 секунд, создавая отдельный DI scope для каждой операции. Задержки повторов растут от 10 до 300 секунд. После восьми попыток автоматические повторы прекращаются; для элементов с `retryable=true` повтор того же HTTP-запроса позволяет продолжить. Постоянные ошибки автоматически не повторяются. После таймаута создания используется прежний syncId; после неопределённого удаления проверяется наличие оригинала.

Merge вызывает endpoint после обычных документов в `ExecuteDocumentChangeAsync`: salesreturn исключены из обычного PUT-переноса. Discovery загружает все страницы позиций salesreturn через Egress и сохраняет полный JSON. Из снимков дубликатов формируется массив `RecreateSalesReturnItem`; новый счёт и договор передаются как `null`, поскольку merge-команда их не содержит.

Полный запрос сохраняется в `catalog_sync.merge_operations.SalesReturnRequestJson` перед вызовом Egress. При повторе отправляются прежние operation ID и весь исходный массив, включая уже успешные элементы. Новая миграция `PersistSalesReturnRecreationRequest` принадлежит CatalogSync. Подтверждённые новые ID и подготовленные данные из ответа Egress атомарно заменяют старые документы и дополнительные данные локального снимка; повторное применение результата безопасно. Частичный успех сохраняется до обработки ошибки, а архивирование дубликатов выполняется только после завершения пересоздания. Общие DTO находятся в `MsContractor.Contracts.Internal`.

Настройки `DOCUMENTS_DISCOVERY`, `DOCUMENTS_PUT_CHANGE`, `DOCUMENTS_CHANGE_AGENT_AND_CONTRACT` остаются независимыми и не управляют этим endpoint. Для переноса salesreturn они должны входить в `DOCUMENTS_DISCOVERY`.

### Интеграционные проверки журнала

Укажите `EGRESS_TEST_POSTGRES` со строкой подключения к отдельному тестовому PostgreSQL и правом `CREATEDB`. Каждый тест создаёт и удаляет собственную БД с префиксом `egress_test_`; базы приложения не используются. Без переменной PostgreSQL-проверки явно помечаются skipped.

```bash
EGRESS_TEST_POSTGRES='Host=localhost;Port=15439;Database=postgres;Username=postgres' \
  dotnet test tests/MsContractor.Sync.Tests/MsContractor.Sync.Tests.csproj -c Release
```

HTTP-проверки используют поддельный транспорт МойСклад и не изменяют реальные документы.
