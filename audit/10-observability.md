# Logging / observability audit

## [O-01] Batch archive request и response логируются целиком

Severity: High

Category: Security

Location: `src/Services/MoySkladEgressService/Services/MoySkladCounterpartyGateway.cs`

Lines: `188-203,278-280,300-315`

Current behavior: Info logs содержат serialized archive payload и полный response body; error path извлекает upstream message.

Problem: Response — полный JSON контрагентов, payload раскрывает их IDs. Logging policy не применяет allowlist/redaction/size limit.

Risk: Business/PII data попадает в централизованные logs с более широким доступом и retention.

Example scenario: Успешный merge пишет все поля archived counterparties в Dozzle/log collector.

Recommended solution: Логировать только count, operation IDs, status, duration и bounded error code; применить centralized redaction и security regression test, запрещающий bodies/tokens.

Priority: P1

Confidence: High

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

## [O-05] Startup и expected client errors логируются непоследовательно

Severity: Low

Category: Code Quality

Location: `src/Services/VendorService/Program.cs`; error middleware в Gateway/Vendor/Egress

Lines: Vendor Program `74-86`; middleware `LogError` branches

Current behavior: Vendor startup использует `Console.WriteLine`; ряд controlled 4xx проходит через Error-level logging.

Problem: Поля не единообразны, client mistakes повышают alert noise.

Risk: Труднее фильтровать startup/dependency incidents и реальные 5xx.

Example scenario: Серия invalid client requests выглядит как server outage в error-rate dashboard.

Recommended solution: Только structured ILogger; 4xx по policy — Information/Warning, 5xx — Error; единый event ID/error code.

Priority: P3

Confidence: Medium
