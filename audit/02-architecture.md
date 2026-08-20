# Architecture audit

## [A-01] DuplicatesMergeService напрямую владеет моделью CatalogSyncService

Severity: High

Category: Architecture

Location: `src/Services/DuplicatesMergeService/MsContractor.DuplicatesMergeService.csproj`; `src/Services/DuplicatesMergeService/Services/MergeProcessor.cs`

Lines: csproj `18-28`; processor `1-24`

Current behavior: Merge service имеет `ProjectReference` на web-проект CatalogSyncService и использует его `CatalogSyncDbContext`, entities, parser и normalizer. Оба сервиса мигрируют одну схему.

Problem: Приватная persistence/business implementation одного сервиса стала compile-time API другого; независимый deploy/schema ownership отсутствует.

Risk: Изменение CatalogSync ломает Merge, одновременные deployments конкурируют за миграции, а ошибки Merge могут напрямую повредить catalog state.

Example scenario: CatalogSync меняет entity mapping; Merge binary требует синхронного обновления и не может быть откатан независимо.

Recommended solution: Явно определить владельца catalog/merge boundary: объединить компоненты в один bounded context либо дать Merge собственное хранилище и versioned internal API/events; shared оставить только contracts.

Priority: P1

Confidence: High

## [A-02] Vendor outbox никогда не публикуется

Severity: High

Category: Architecture

Location: `src/Services/VendorService/Services/VendorInstallationService.cs`; `src/Services/VendorService/Program.cs`

Lines: service `97-117,163-177`; Program `1-102`

Current behavior: Activation/deactivation атомарно записывают `vendor.outbox_messages`, но в VendorService нет hosted publisher и Kafka producer для этой таблицы.

Problem: Outbox является терминальным хранилищем, а не механизмом доставки.

Risk: Notification/Audit и любые lifecycle consumers никогда не узнают об install/suspend/uninstall; таблица растёт.

Example scenario: Пользователь удаляет приложение, БД Vendor меняется, но событие `InstallationUninstalled` навсегда остаётся `PublishedAt=null`.

Recommended solution: Добавить owner-specific outbox publisher с idempotent event contract, claim/backoff/monitoring и тестом crash-after-publish-before-mark.

Priority: P1

Confidence: High

## [A-03] Sync и merge не координируются на уровне аккаунта

Severity: High

Category: Architecture

Location: `src/Services/CatalogSyncService/Services/SyncProcessor.cs`; `src/Services/DuplicatesMergeService/Services/MergeProcessor.cs`

Lines: Sync `74-101,113-174`; Merge `30-91`

Current behavior: Kafka ordering применяется внутри каждого topic/group, но sync consumer и merge consumer работают независимо и меняют одни `counterparties`. Distributed account lock/write priority отсутствуют.

Problem: Account key не сериализует операции между разными topics/consumer groups.

Risk: Full sync может удалить/перезаписать локальный результат merge; incremental sync и merge используют last-write-wins.

Example scenario: Merge успешно архивирует duplicate, пока full sync уже удалил строки и позже вставляет snapshot, полученный до archive.

Recommended solution: Ввести account-scoped coordination с приоритетом merge write, fencing token/lease и проверяемой ownership; определить reconciliation после внешней записи.

Priority: P1

Confidence: High

## [A-04] NotificationService и AuditService являются heartbeat-заглушками

Severity: Medium

Category: Architecture

Location: `src/Services/NotificationService/Worker.cs`; `src/Services/AuditService/Worker.cs`

Lines: оба файла `3-14`

Current behavior: Сервисы раз в секунду логируют текущее время; Kafka/DB business consumers и audit persistence отсутствуют.

Problem: Архитектурно заявленные компоненты не выполняют свою функцию, но создают видимость работающих healthy services.

Risk: Нет пользовательских уведомлений и независимого аудита критических операций; health checks остаются зелёными.

Example scenario: Merge завершился с partial failure, но NotificationService не читает событие и пользователь не получает результат.

Recommended solution: До production либо реализовать и тестировать contracts/consumers/storage, либо не включать заглушки в production deployment/readiness.

Priority: P2

Confidence: High
