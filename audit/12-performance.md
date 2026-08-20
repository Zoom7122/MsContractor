# Performance audit

## [P-01] Duplicate preview загружает весь активный tenant catalog в память

Severity: High

Category: Performance

Location: `src/Services/DuplicatesMergeService/Services/DuplicatePreviewService.cs`

Lines: `18-52`

Current behavior: Первый query читает все active rows аккаунта, grouping выполняется in-memory; второй query загружает все совпавшие rows и RawJson. Pagination/result limit отсутствуют.

Problem: Работа и memory растут линейно с tenant size, хотя индексы normalized fields могли бы выполнять grouping в БД.

Risk: OOM/GC pauses, большие HTTP responses и timeout Gateway 15s.

Example scenario: Аккаунт с сотнями тысяч контрагентов открывает duplicate view и материализует весь набор на каждом запросе.

Recommended solution: SQL grouping/account-scoped indexes, cursor pagination, result caps и отдельная selection-preview загрузка только выбранных IDs.

Priority: P1

Confidence: High

## [P-02] Full sync буферизует полный RawJson snapshot перед записью

Severity: High

Category: Performance

Location: `src/Services/CatalogSyncService/Services/SyncProcessor.cs`; `src/Services/CatalogSyncService/Models/MoySkladModels.cs`

Lines: processor `183-197,213-246`; models `1-31`

Current behavior: Active+archived pages последовательно добавляются в `List<ParsedCounterparty>(totalCount)`; каждый item содержит parsed model и исходный JSON string. Затем создаётся второй массив entities.

Problem: Peak memory содержит несколько представлений всего tenant catalog.

Risk: OOM/container restart и Kafka duplicate processing на больших аккаунтах.

Example scenario: Большие additional fields делают RawJson многокилобайтным; 200k rows одновременно живут как strings/models/entities.

Recommended solution: Staging/bulk streaming по pages с snapshot version, bounded memory и atomic publish/swap после validation.

Priority: P1

Confidence: High

## [P-03] Merge делает SaveChanges для каждой archive operation

Severity: Medium

Category: Performance

Location: `src/Services/DuplicatesMergeService/Services/MergeProcessor.cs`

Lines: `94-100,151-185`

Current behavior: `MarkRunningAsync` вызывается по одному и каждый раз делает DB roundtrip; failure handling также сохраняет каждую operation отдельно.

Problem: O(N) транзакций/roundtrips перед одним batch HTTP request.

Risk: Высокая latency, pool pressure и больший шанс crash в промежуточном state.

Example scenario: 900 duplicates создают не менее 900 SaveChanges до archive POST.

Recommended solution: Bulk state transition одним SaveChanges/ExecuteUpdate, сохраняя per-operation IDs и durable transaction boundary.

Priority: P2

Confidence: High

## [P-04] RawJson дублирует upstream payload в storage и response path

Severity: Medium

Category: Performance

Location: `src/Services/CatalogSyncService/Repo/Counterparty.cs`; `src/Services/CatalogSyncService/Repo/CatalogSyncDbContext.cs`; `src/Services/DuplicatesMergeService/Services/DuplicatePreviewService.cs`

Lines: entity `22`; mapping `62`; preview `36-41,80-94`

Current behavior: Полный JSONB сохраняется вместе с extracted columns, затем читается, повторно parse-ится и сериализуется в preview.

Problem: Большие rows увеличивают WAL/storage/query materialization без доказанного UI use case.

Risk: Медленные sync/backup/preview и лишний browser payload.

Example scenario: Незначительное изменение counterparty переписывает большой JSONB и затем отправляет его для duplicate list.

Recommended solution: Определить обязательные audit/reconciliation fields; убрать raw из hot query/DTO, при необходимости вынести compressed/versioned raw archive с retention.

Priority: P2

Confidence: High
