# HTTP / Egress audit

Опубликованные internal ports описаны как `C-03`, plaintext token transport — `S-03`, rate limiter — `R-01`. Ручного production `new HttpClient()` не найдено; клиенты зарегистрированы через `AddHttpClient` и обычно передают `CancellationToken`.

## [H-01] Timeout Vendor token client не маппится в controlled Egress error

Severity: Medium

Category: HTTP

Location: `src/Services/MoySkladEgressService/Services/VendorTokenClient.cs`

Lines: `28-43,77-85`

Current behavior: Client оборачивает `HttpRequestException`, но не `OperationCanceledException/TaskCanceledException`, когда сработал его 10-second HttpClient timeout и caller token не cancelled.

Problem: В отличие от MoySklad gateways, timeout token lookup уходит как необработанное исключение.

Risk: Internal controller возвращает generic 500 вместо stable `503 ACCESS_TOKEN_UNAVAILABLE`; logs/alerts и retry classification расходятся.

Example scenario: Vendor TCP connection зависает на 10 секунд — Egress middleware не получает `EgressException` и формирует 500.

Recommended solution: Отдельно оборачивать timeout при `!cancellationToken.IsCancellationRequested`, сохраняя caller cancellation без преобразования; добавить test.

Priority: P2

Confidence: High

## [H-02] Sync HTTP hop теряет correlation ID

Severity: Medium

Category: HTTP

Location: `src/Services/Gateway.Bff/Services/CatalogSyncClient.cs`; `src/Services/CatalogSyncService/Controllers/InternalSyncController.cs`

Lines: client `16-31`; controller `18-42`

Current behavior: Gateway correlation middleware создаёт ID, но CatalogSyncClient передаёт только API key/body. Internal controller генерирует MessageId/SyncRunId и contract не содержит correlation.

Problem: HTTP trace context обрывается до Kafka, в отличие от merge flow.

Risk: Incident невозможно надёжно связать от browser request до Egress/MoySklad logs.

Example scenario: Один account запускает два sync; по Gateway correlation нельзя определить соответствующий failed Kafka message.

Recommended solution: Передавать/валидировать `X-Correlation-Id`, сохранять его в sync run и versioned message envelope, включать во все scopes.

Priority: P2

Confidence: High

## [H-03] Internal correlation headers не имеют ограничения длины

Severity: Low

Category: HTTP

Location: `src/Services/MoySkladEgressService/Controllers/InternalCounterpartiesController.cs`; `src/Services/MoySkladEgressService/Controllers/InternalDocumentsController.cs`

Lines: методы чтения `X-Correlation-Id` в обоих controllers

Current behavior: Непустая строка принимается как correlation ID и попадает в structured logs; GUID/максимальная длина не требуются.

Problem: Компрометированный internal caller может создавать oversized/high-cardinality log fields.

Risk: Log ingestion cost/noise; прямое выполнение log injection не подтверждено structured logger-ом.

Example scenario: Internal request передаёт сотни килобайт correlation header в пределах proxy limits.

Recommended solution: Принимать UUID/traceparent или bounded ASCII token; invalid значение заменять server-generated ID.

Priority: P3

Confidence: Medium
