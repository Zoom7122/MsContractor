# Remediation roadmap

Подробные альтернативы и причины выбора по каждому finding: [16-solution-options.md](16-solution-options.md).

## P0 — немедленно остановить возможность компрометации/потери данных

1. `C-01`: отозвать/ротировать все найденные secrets и credentials; очистку Git history выполнять только после подтверждения rotation.
2. `C-02`: отделить production manifest, выставить `Production`, физически исключить dev-session route.
3. `C-03/C-04`: закрыть host/network access к internal APIs, PostgreSQL, Redis, Kafka и UI; затем включить service/data-plane auth/TLS/ACL.
4. `R-01/M-03`: реализовать Redis-backed limiter, `blocked_until` и merge-write priority до масштабирования sync.
5. `D-01`: заменить delete-before-fetch на staging + atomic swap, сохраняя last-known-good snapshot.

Exit criteria: rotated credentials; external port scan не видит internal/data ports; production `/session/dev` и OpenAPI admin routes недоступны; fault до/во время full sync не удаляет рабочий каталог; limiter tests проходят на нескольких instances.

## P1 — обеспечить durable и идемпотентные workflows

1. `K-01/D-03`: durable sync/merge acceptance, client idempotency keys, pending operation + outbox до `202`.
2. `A-03/D-02`: account-scoped lock/fencing, optimistic concurrency и явный merge-over-sync priority.
3. `M-01/M-02`: bounded/chunked batches, per-item results и reconciliation ambiguous outcomes.
4. `S-01/R-03`: durable session revocation/versioning и atomic Redis race handling.
5. `K-02/K-03`: consumer poll-safe execution, bounded backoff/jitter/DLQ, official rate hints.
6. `DC-01`: persistent replicated Kafka с явными topic policies.
7. `A-01/A-02`: восстановить service ownership и запустить Vendor outbox delivery.
8. `O-01/S-05/S-03/S-04/DC-02/DC-03`: убрать sensitive bodies/raw DTO, минимизировать secrets, защитить transport/build/admin UI.
9. `P-01/P-02`: bounded DB grouping/pagination и streaming staging snapshot.
10. `T-01`: добавить real-infrastructure failure/concurrency suite как release gate.

Exit criteria: повтор HTTP/Kafka сообщения даёт один logical operation; concurrent sync/merge deterministic; mixed archive result корректно фиксирует каждый duplicate; broker/container failure не теряет accepted command.

## P2 — эксплуатационная устойчивость и наблюдаемость

- `K-04/K-05/K-06`: Sync DLQ, outbox claim/lease, versioned contracts.
- `D-04/D-05/D-06/D-07`: подтвердить UUID scope, retention, migration owner и callback tombstones.
- `R-02`: корректная sliding session touch policy.
- `H-01/H-02/M-04`: stable timeout mapping, correlation propagation, bounded discovery selection.
- `DC-04/DC-05/DC-06`: non-root/minimal images, readiness semantics, host/internal port split.
- `O-02/O-03/O-04`: traces/metrics/SLO и удаление heartbeat noise.
- `T-02/T-03/T-04`: green cookie tests, честный Redis integration status, mixed partial failure tests.
- `P-03/P-04/Q-01/Q-02`: сократить DB roundtrips/raw payload, формализовать state machine и phone normalization.
- `S-02/S-06`: CSRF controls и production guard OpenAPI.

Exit criteria: один trace проходит Gateway→Kafka→Egress; dashboards показывают outbox/consumer/job/rate state; all test suites green; readiness red при потере обязательной dependency.

## P3 — hygiene и снижение операционной неоднозначности

- `H-03`: bounded correlation header.
- `O-05`: единая structured logging severity policy.
- `Q-03/Q-04`: синхронизировать Makefile/README и убрать generated artifacts.
- `E-01/E-02`: очистить env surface и убрать weak production fallbacks.

## Последовательность безопасного внедрения

Не совмещать secret rotation/network closure с крупным code refactor в одном rollout. Сначала закрыть exposure и ввести наблюдаемость, затем менять durable state machines под fault-injection tests. Миграции для idempotency/locks/staging должны быть backward-compatible и выполняться отдельным migration owner (`D-06`).

## Что требует дополнительной проверки

- Глобальна ли уникальность UUID entity между аккаунтами МойСклада (`D-04`).
- Есть ли вне repository ingress/firewall/TLS, external OpenTelemetry и secret manager policies — Not verified.
- Реальное число/размер counterparties и latency для выбора конкретных limits — нужны production metrics без чувствительных payload.
- Frontend production build — Not verified, потому что `npm` отсутствует и установка зависимостей запрещена условиями задачи.
- Dependency CVE/SBOM scan — Not verified; network/package installation не выполнялись.
