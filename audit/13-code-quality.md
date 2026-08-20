# Code quality audit

## [Q-01] Критичные workflows сосредоточены в крупных классах и magic statuses

Severity: Medium

Category: Code Quality

Location: `src/Services/CatalogSyncService/Services/SyncProcessor.cs`; `src/Services/MoySkladEgressService/Services/MoySkladCounterpartyGateway.cs`; merge entities/services

Lines: SyncProcessor `1-534`; gateway `1-414`; string statuses по соответствующим files

Current behavior: Pagination, state machine, persistence, parsing и outbox собраны в крупных services; sync statuses/error codes — строковые literals, merge — частично constants.

Problem: Invariants и transition rules распределены по methods и не проверяются типовой state machine.

Risk: Неполные transitions при новых failure cases, сложный review и дублирование HTTP/error mapping.

Example scenario: Добавляется новый terminal status, но один consumer/outbox filter не обновляется.

Recommended solution: После P0/P1 выделить state transition services/value types и pure page validators; не переносить service-private implementation в Shared.

Priority: P2

Confidence: High

## [Q-02] Phone normalization сохраняет punctuation и пробелы

Severity: Medium

Category: Code Quality

Location: `src/Services/CatalogSyncService/Services/CounterpartyNormalizer.cs`

Lines: `77-87`

Current behavior: Нормализатор trim/lowercase и заменяет начальную `8` на `+7`, но не удаляет `()`, `-`, spaces.

Problem: Эквивалентные номера получают разные normalized keys и не попадают в одну duplicate group.

Risk: Пропущенные дубли и недостоверный UI результат.

Example scenario: `8 (999) 000-00-00` и `+79990000000` остаются различными.

Recommended solution: Согласовать phone canonicalization/region policy, хранить raw отдельно, добавить property/boundary tests.

Priority: P2

Confidence: High

## [Q-03] Makefile и README расходятся с реальными targets

Severity: Low

Category: Code Quality

Location: `Makefile`; `README.md`

Lines: Makefile `1-34`; README команды запуска

Current behavior: `.PHONY` перечисляет отсутствующие `compose/ps/build`, пропускает часть реальных targets; `dozzle` добавляет второй `up -d` к уже собранной команде. README документирует отсутствующие targets.

Problem: Developer runbook не воспроизводим.

Risk: Ошибки локального/операционного запуска, использование destructive `down -v` по догадке.

Example scenario: `make dozzle` формирует некорректную последовательность аргументов Compose.

Recommended solution: Синхронизировать targets/PHONY/README и добавить shell/Make smoke check.

Priority: P3

Confidence: High

## [Q-04] В Git отслеживаются Python bytecode artifacts

Severity: Low

Category: Code Quality

Location: `scripts_for_test_data/__pycache__/*.pyc`

Lines: binary files

Current behavior: Generated `.pyc` входят в repository.

Problem: Артефакты зависят от interpreter/platform и не являются source.

Risk: Шум diff, ненужный binary provenance и разрастание repository.

Example scenario: Запуск script другой Python-версией постоянно меняет binary files.

Recommended solution: После отдельного согласования удалить tracked artifacts и добавить `__pycache__/`, `*.pyc` в ignore.

Priority: P3

Confidence: High
