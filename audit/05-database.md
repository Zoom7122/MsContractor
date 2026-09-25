# PostgreSQL / EF Core audit

## [D-01] Full sync удаляет рабочий каталог до загрузки snapshot

Severity: High

Category: DB

Location: `src/Services/CatalogSyncService/Services/SyncProcessor.cs`

Lines: `74-101`

Current behavior: Account-scoped `ExecuteDeleteAsync` коммитится до первого Egress request; новая выборка и insert происходят позже.

Problem: Старый корректный snapshot не сохраняется до готовности и валидации нового.

Risk: Любой timeout, 429, malformed page или process crash оставляет аккаунт с пустым каталогом и без duplicate preview.

Example scenario: После delete VendorService временно недоступен; run становится failed, а все counterparties уже удалены.

Recommended solution: Загружать staging snapshot, валидировать, затем атомарно swap/upsert в транзакции; старую версию удалять только после успешной фиксации.

Priority: P0

Confidence: High

## [D-02] Нет optimistic concurrency для общих строк catalog/merge

Severity: High

Category: DB

Location: `src/Services/CatalogSyncService/Repo/CatalogSyncDbContext.cs`; `src/Services/CatalogSyncService/Repo/Counterparty.cs`

Lines: DbContext `48-74,88-115`; entity `1-28`

Current behavior: Counterparty, merge job и operations не имеют row version/xmin concurrency token; writers сохраняют tracked state last-write-wins.

Problem: Параллельные sync/merge/retry не обнаруживают, что исходная версия изменилась.

Risk: Потерянное обновление, откат archived/name/phone и неверный terminal job status.

Example scenario: Incremental sync читает строку, merge обновляет её, затем sync SaveChanges перезаписывает результат старым snapshot.

Recommended solution: Добавить optimistic concurrency/fencing и explicit conflict reconciliation; критические account flows дополнительно сериализовать.

Priority: P1

Confidence: High

## [D-03] Merge POST не идемпотентен

Severity: High

Category: DB

Location: `src/Services/DuplicatesMergeService/Services/MergeJobCreator.cs`

Current behavior: Каждый POST создаёт новые job/message IDs; idempotency key отсутствует.

Problem: Повтор POST после потери ответа не возвращает уже созданный job.

Risk: Повторная отправка получает конфликт вместо идентификатора существующей операции; пользователь не знает, что merge уже запущен.

Example scenario: Gateway timeout после commit; frontend повторяет POST и получает ошибку.

Recommended solution: Client idempotency key с unique `(account_id,key)`; повтор возвращает существующий job.

Priority: P1

Confidence: High

## [D-04] Counterparty primary key не включает account_id

Severity: Medium

Category: DB

Location: `src/Services/CatalogSyncService/Repo/CatalogSyncDbContext.cs`

Lines: `48-64`

Current behavior: PK — только `Id`; unique `(AccountId, Id)` поэтому избыточен. Все прикладные SELECT корректно фильтруют account.

Problem: Potential issue: если MoySklad UUID гарантирован уникальным только внутри account, одинаковый UUID двух tenants конфликтует до account-scoped unique constraint.

Risk: Один tenant не синхронизируется из-за строки другого tenant; прямого чтения чужих данных в проверенном коде не найдено.

Example scenario: Два sandbox accounts импортируют сущность с одним UUID, второй insert получает PK violation.

Recommended solution: Подтвердить глобальную гарантию UUID у МойСклада. Если её нет — перейти на composite PK `(AccountId, Id)` и согласованные FKs.

Priority: P2

Confidence: Medium

## [D-05] Нет retention/cleanup для operation tables и RawJson

Severity: Medium

Category: DB

Location: `src/Services/CatalogSyncService/Repo/CatalogSyncDbContext.cs`; `src/Services/VendorService/Repo/VendorDbContext.cs`

Lines: Catalog `20-115`; Vendor outbox mapping `119-146`

Current behavior: Inbox, outbox, sync runs, merge jobs/operations и Vendor outbox не имеют cleanup worker/partition/retention policy; counterparties хранят полный JSONB.

Problem: Append-heavy operational state растёт без границы.

Risk: Увеличение индексов, vacuum/backup time и ухудшение outbox polling.

Example scenario: Миллионы published outbox rows остаются в индексе `(PublishedAt,CreatedAt)` и замедляют maintenance.

Recommended solution: Определить compliance retention, архивирование/partitioning и idempotent scheduled cleanup с метриками table size.

Priority: P2

Confidence: High

## [D-06] Два сервиса запускают миграции одной схемы при startup

Severity: Medium

Category: DB

Location: `src/Services/CatalogSyncService/Program.cs`; `src/Services/DuplicatesMergeService/Program.cs`

Lines: Catalog `43-50`; Duplicates `40-48`

Current behavior: Обе replicas используют `CatalogSyncDbContext` и вызывают `MigrateAsync()` при старте.

Problem: Potential issue: deployment orchestration не выделяет единственного migration owner; availability зависит от DB migration locking и порядка версий.

Risk: Startup contention или запуск старого binary после новой несовместимой migration.

Example scenario: Catalog vNext и Merge vCurrent стартуют одновременно; один применяет schema, которую второй binary не понимает.

Recommended solution: Выносить migration в отдельный versioned deployment job и проверять backward compatibility rolling deploy.

Priority: P2

Confidence: Medium

## [D-07] Deactivation неизвестной installation не сохраняет request idempotency

Severity: Medium

Category: DB

Location: `src/Services/VendorService/Services/VendorInstallationService.cs`; `src/Services/VendorService/Repo/VendorInstallationRepository.cs`

Lines: service `143-152`; repository `33-42`

Current behavior: При отсутствии installation вызывается `SaveAsync(requestId, null, null)`. Repository сразу коммитит пустую транзакцию и не записывает `RequestId`.

Problem: Callback отвечает как обработанный, но durable idempotency record отсутствует.

Risk: Повторы одного callback не распознаются, а тот же request ID позже может быть связан с другим событием.

Example scenario: МойСклад повторяет uninstall для неизвестного account; каждый раз выполняется Redis revoke, но audit/idempotent replay остаётся false.

Recommended solution: Сохранять tombstone/inbox callback record даже без installation, с compatible payload hash и unique request ID.

Priority: P2

Confidence: High
