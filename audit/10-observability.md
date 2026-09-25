# Logging / observability audit

## [O-02] Sync trace нельзя связать end-to-end

Severity: Medium

Category: Code Quality

Location: `src/Services/Gateway.Bff/Middleware/VendorRequestCorrelationMiddleware.cs`; `src/Shared/MsContractor.Contracts/SyncContracts.cs`; `src/Services/CatalogSyncService/Services/SyncProcessor.cs`

Lines: middleware correlation creation/response; contract `18-44`; processor scope `45-59`

Current behavior: Gateway имеет correlation ID, но sync message не содержит его; processor фактически использует message/run identifiers. Merge correlation сохранён корректно.

Problem: Нельзя выполнить единый distributed query Gateway → Catalog → Kafka → Egress.

Risk: Дольше MTTR и неразрешимая неоднозначность при нескольких операциях аккаунта.

Example scenario: Upstream 429 найден в Egress log, но отсутствует ID исходного browser request.

Recommended solution: Propagate W3C trace context/correlation through HTTP headers, DB operation и Kafka headers/envelope; сохранять trace/span IDs.

Priority: P2

Confidence: High

## [O-03] Метрики и distributed tracing отсутствуют

Severity: Medium

Category: Code Quality

Location: все `src/Services/*/Program.cs`; все `.csproj`

Lines: registrations целиком

Current behavior: Используются ILogger и health endpoints; OpenTelemetry/metrics exporters, Kafka lag, outbox lag, rate-budget и job duration metrics в коде/package references не найдены.

Problem: Not verified вне репозитория: внешняя platform instrumentation может существовать, но application-level business telemetry отсутствует.

Risk: Невозможно заблаговременно увидеть backlog, stuck job, rate-limit saturation и SLO regression.

Example scenario: Merge outbox перестаёт публиковаться, при этом health остаётся green и alert отсутствует.

Recommended solution: Добавить OpenTelemetry traces/metrics с low-cardinality labels и dashboards/alerts для lag, attempts, states, 429, latency и dependency health.

Priority: P2

Confidence: Medium

## [O-04] Placeholder workers создают log event каждую секунду

Severity: Medium

Category: Performance

Location: `src/Services/NotificationService/Worker.cs`; `src/Services/AuditService/Worker.cs`

Lines: оба `5-14`

Current behavior: Каждый instance бесконечно пишет Info heartbeat раз в секунду.

Problem: Сообщение не доказывает business readiness и создаёт постоянный шум.

Risk: Стоимость log storage, вытеснение полезных событий, ложная видимость активности.

Example scenario: 10 replicas генерируют около 864000 бесполезных записей в сутки.

Recommended solution: Удалить periodic log, использовать health/metrics и логировать только lifecycle/state transitions.

Priority: P2

Confidence: High

## [O-05] Expected upstream 4xx логируются как Error

Severity: Low

Category: Code Quality

Location: `src/Services/MoySkladEgressService/ResponseHandling/MoySkladResponseHandler.cs`

Current behavior: Любой не-2xx ответ МойСклад, включая ожидаемые бизнес-отказы, логируется уровнем Error.

Problem: Client/business mistakes повышают alert noise.

Risk: Труднее отделить реальные 5xx от ожидаемых отказов.

Example scenario: Серия отказов по закрытому периоду выглядит как outage в error-rate dashboard.

Recommended solution: Upstream 4xx — Warning/Information по policy, 5xx — Error; единый event ID/error code.

Priority: P3

Confidence: Medium
