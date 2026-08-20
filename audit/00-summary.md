# Технический аудит mscontractor

Дата среза: 2026-08-20. Аудит выполнен по текущему рабочему дереву, включая незакоммиченные файлы; production-код, конфигурация и зависимости не изменялись.

Варианты устранения и обоснование предпочтительного решения для каждого finding собраны в [16-solution-options.md](16-solution-options.md).

## Project health

Система имеет хорошие локальные основы — account-scoped запросы в прикладном коде, шифрование access token в PostgreSQL, typed `HttpClient`, manual Kafka commit, inbox/outbox для merge и детальная проверка discovery pagination. При этом текущий Compose нельзя считать production-ready: секреты находятся в Git, окружение принудительно `Development`, internal API и хранилища опубликованы на host, Redis rate limiter фактически пустой. В sync/merge есть подтверждённые окна потери/рассинхронизации состояния.

Итоговая оценка: **высокий риск production-развёртывания до закрытия P0/P1**.

## Findings

- Critical: 4
- High: 24
- Medium: 28
- Low: 6

Info-наблюдения и результаты запуска проверок в эти числа не включены.

## Top risks

1. Секреты и пароль тестовой учётной записи закоммичены и присутствуют в Git history (`C-01`).
2. Compose всегда запускает backend в `Development`, из-за чего публично доступен dev-session endpoint (`C-02`).
3. Internal API опубликованы наружу и защищены одним уже скомпрометированным ключом; возможен выбор чужого `accountId` (`C-03`).
4. PostgreSQL, Redis, Kafka и административные UI опубликованы без production-защиты (`C-04`).
5. Реального distributed rate limiter нет; sync не уступает merge write operations (`R-01`).
6. Full sync удаляет каталог до первого запроса в МойСклад (`D-01`).
7. Sync возвращает `202` после прямой Kafka-публикации без durable operation/outbox (`K-01`).
8. Sync и merge не имеют межпроцессной координации и optimistic concurrency (`A-03`, `D-02`).
9. Batch archive не реализует partial failure по отдельным дублям (`M-02`).
10. Kafka одноброкерная, replication factor 1, volume не подключён (`DC-01`).

## Architecture violations

- DuplicatesMergeService напрямую использует DbContext/entities и схему CatalogSyncService.
- Vendor outbox создаётся, но publisher отсутствует.
- Sync HTTP acceptance не создаёт operation/outbox в PostgreSQL до Kafka.
- Redis заявлен как distributed rate-limit runtime-state, но limiter не хранит состояние и не блокирует запросы.
- NotificationService и AuditService остаются heartbeat-заглушками.

Прямых вызовов JSON API 1.2 из Sync/Merge не найдено; они идут через Egress. Access token в Kafka-контрактах и frontend-коде не найден.

## Security risks

- Git содержит секреты; dev/prod используют одинаковые значения для общих чувствительных переменных.
- Dev authentication route доступен при текущей production-конфигурации.
- Наружу опубликованы internal endpoints и stateful infrastructure.
- Один общий env-блок раздаёт DB password/internal key сервисам без необходимости.
- Access token передаётся Egress по plaintext HTTP внутри Docker network.
- Batch request/response МойСклада логируются целиком.
- Нет явной CSRF-защиты cookie-authenticated state-changing endpoints.

## Reliability risks

- Ошибка после предварительного удаления оставляет full-sync каталог пустым.
- Нет durable HTTP acceptance и request idempotency для sync/merge.
- Длительные consumer handlers могут превысить стандартный `max.poll.interval.ms`.
- Outbox publishers не claim-ят строки при нескольких instances.
- Merge retry — фиксированные 2 секунды без `Retry-After`/`blocked_until`.
- Kafka state теряется при пересоздании контейнера.
- Ошибка Redis после deactivation оставляет ранее созданные сессии пригодными для Gateway.

## Missing tests

- Нет end-to-end теста Gateway → Kafka → Egress → PostgreSQL.
- Нет реального Kafka теста commit/rebalance/DLQ/duplicate delivery/outbox multi-instance.
- PostgreSQL semantics проверяются преимущественно через SQLite; нет Testcontainers/concurrency.
- Redis integration test молча считается успешным без `TEST_REDIS_CONNECTION`.
- Нет тестов параллельных sync/merge, повторной HTTP-отправки и recovery после response loss.
- Нет frontend test suite; production build не запущен из-за отсутствия `npm`.
- Один существующий VendorService test падает на cookie policy.

## Quick wins

- Немедленно отозвать/ротировать найденные секреты, затем очистить Git history.
- Перенести dev route и Swagger/UI под environment/profile guard.
- Убрать host port publishing у internal services/data stores или bind к localhost для dev.
- Удалить полные request/response body из production logs.
- Подключить Kafka volume и разделить production Compose/profile.
- Явно настроить `MaxPollIntervalMs` и retry/backoff policy.
- Сделать Redis integration test явным skip/fail по профилю.

## Recommended order

### P0

`C-01` → `C-02` → `C-03` → `C-04` → `R-01` → `D-01`.

### P1

Durable sync acceptance/idempotency, sync/merge locks, deactivation consistency, Kafka durability, batch limits/partial failure, sensitive logging, service boundaries.

### P2

Consumer rebalance settings, outbox claiming, session sliding/revoke atomicity, correlation/tracing, retention, bounded preview/snapshot processing, integration tests.

### P3

Makefile/README/env cleanup, structured startup logging, repository hygiene and maintainability refactoring.
