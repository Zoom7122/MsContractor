# Test audit

Фактический запуск: `dotnet build MsContractor.sln --no-restore` — успешно, 0 warnings/0 errors. `dotnet test MsContractor.sln --no-build` — Sync 101/101, Vendor 21/22, один fail. `npm run build` не запускался: `npm` отсутствует, зависимости по ограничению не устанавливались.

## [T-01] Critical distributed flows не покрыты real-infrastructure tests

Severity: High

Category: Testing

Location: `tests/MsContractor.Sync.Tests`; `tests/MsContractor.VendorService.Tests`

Lines: test projects целиком

Current behavior: Есть хорошие fake HTTP/unit/service tests, включая discovery pagination/429/cancellation. Нет Testcontainers Kafka/PostgreSQL suite, реального commit/rebalance/DLQ/outbox multi-instance, полного Gateway→consumer→Egress flow, concurrent sync/merge и response-loss idempotency.

Problem: SQLite/fakes не воспроизводят PostgreSQL locks/constraints, Kafka group behavior и сетевые ambiguous failures.

Risk: Главные findings `K-01/K-02/D-02/D-03/M-02` проходят CI незамеченными.

Example scenario: Unit test подтверждает manual commit path, но не обнаруживает rebalance после пяти минут handler execution.

Recommended solution: Добавить tiered Testcontainers suite и deterministic fault injection; отдельно account-isolation/concurrency/idempotency/security scenarios.

Priority: P1

Confidence: High

## [T-02] Текущий Vendor test suite падает на cookie policy

Severity: Medium

Category: Testing

Location: `tests/MsContractor.VendorService.Tests/MoyskladSessionControllerTests.cs`; `src/Services/VendorService/Controllers/MoyskladSessionController.cs`

Lines: test `35-65`; controller `155-164`

Current behavior: Test для Development/non-forwarded HTTPS ожидает `SameSite=Lax` без Secure; controller всегда выставляет `SameSite=None; Secure=true`. Результат запуска: 1 failed из 22.

Problem: Реализация и зафиксированная security policy расходятся; CI baseline не green.

Risk: Неясна intended cookie semantics локально/за proxy; будущие regressions теряются среди известного fail.

Example scenario: HTTP dev iframe не получает Secure cookie, хотя тест явно требует environment-appropriate behavior.

Recommended solution: Согласовать browser deployment contract, изменить либо implementation, либо test только после security review; CI должен снова стать zero-failure.

Priority: P2

Confidence: High

## [T-03] Redis integration test молча проходит без Redis

Severity: Medium

Category: Testing

Location: `tests/MsContractor.VendorService.Tests/VendorSessionStoreRedisTests.cs`

Lines: `10-17`

Current behavior: Если `TEST_REDIS_CONNECTION` отсутствует, test делает `return` и отмечается passed, не skipped.

Problem: Отчёт CI создаёт ложное подтверждение Redis integration coverage.

Risk: Race/TTL/transaction regression проходит при фактическом запуске только unit части.

Example scenario: Pipeline забывает env; dashboard показывает 22 tests, включая зелёный Redis test, который не выполнял assertions.

Recommended solution: Отдельная integration category с явным prerequisite/skip reason либо обязательный Testcontainer в integration pipeline.

Priority: P2

Confidence: High

## [T-04] Partial failure test закрепляет all-or-nothing ошибку batch

Severity: Medium

Category: Testing

Location: `tests/MsContractor.Sync.Tests/MergeProcessorTests.cs`

Lines: `64-85`

Current behavior: Test с названием `ArchiveFailureContinuesAndProducesPartialCompletion` ожидает, что при ошибке одного duplicate обе operations failed и оба остаются unarchived.

Problem: Тест не моделирует mixed upstream response и фактически закрепляет проблему `M-02`.

Risk: Реализация считается partial-failure-safe, хотя успех отдельного элемента теряется.

Example scenario: Исправление per-item reconciliation сломает текущий test и может быть ошибочно отклонено.

Recommended solution: Добавить mixed result/timeout-after-partial-apply cases и assertions по каждой operation; переименовать all-batch failure scenario.

Priority: P2

Confidence: High
