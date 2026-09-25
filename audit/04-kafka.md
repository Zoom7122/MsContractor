# Kafka audit

## [K-01] Sync возвращает 202 без durable operation/outbox

Severity: High

Category: Kafka

Location: `src/Services/CatalogSyncService/Controllers/InternalSyncController.cs`; `src/Services/CatalogSyncService/Services/SyncProcessor.cs`

Lines: controller `32-48`; processor `280-330`

Current behavior: HTTP action создаёт IDs, напрямую вызывает producer и возвращает `202`. `sync_runs` создаётся только consumer-ом после получения сообщения.

Problem: Между HTTP acceptance, Kafka и PostgreSQL нет атомарной durable boundary; у POST отсутствует request/idempotency key.

Risk: Потерянный response вызывает повторный sync; сбой/retention Kafka оставляет принятый клиентом ID без записи состояния.

Example scenario: Produce успешно завершён, соединение рвётся до 202; frontend повторяет POST и запускает второй full sync.

Recommended solution: Сначала атомарно создать `sync_run=pending` и command outbox с idempotency key в PostgreSQL, затем публиковать; повтор POST должен возвращать тот же operation.

Priority: P1

Confidence: High

## [K-02] Длительные handlers не настраивают max.poll.interval.ms

Severity: High

Category: Kafka

Location: `src/Services/CatalogSyncService/Services/SyncRequestedConsumer.cs`; `src/Services/DuplicatesMergeService/Services/MergeRequestedConsumer.cs`

Lines: Sync `14-23,86-100`; Merge `18-28,66-89`

Current behavior: Один `Consume()` сопровождается полным sync/merge до следующего poll. У sync consumer `MaxPollIntervalMs` не задан (действует client default); merge выполняет несколько шагов по 10 минут каждый, что превышает заданный интервал.

Problem: Apache Kafka считает consumer failed, если poll не происходит до истечения interval, и инициирует rebalance; официальный default — 300000 ms. [Apache Kafka consumer configuration](https://kafka.apache.org/41/generated/consumer_config.html)

Risk: Rebalance во время большого sync, commit из потерянной assignment, duplicate processing и лишние запросы в МойСклад.

Example scenario: Full sync занимает 8 минут; partition передаётся второму instance, который начинает тот же destructive flow.

Recommended solution: Настроить interval под верхнюю границу, ограничивать работу/делить её на durable steps, обрабатывать partition revoke и добавить rebalance integration test.

Priority: P1

Confidence: High

## [K-03] Merge retry использует бесконечный фиксированный loop

Severity: High

Category: Kafka

Location: `src/Services/DuplicatesMergeService/Services/MergeRequestedConsumer.cs`; `src/Services/DuplicatesMergeService/Services/MergeProcessor.cs`

Lines: consumer `81-89`; processor `162-189,279-283`

Current behavior: Retryable processor failure делает `Seek` и ждёт 2 секунды. Delay не учитывает `Retry-After`, jitter или shared `blocked_until`; исчерпание attempts фиксируется лишь внутри operations.

Problem: На 429/долгой деградации consumer повторяет запросы с одинаковым темпом, блокируя partition для этого и следующих accounts.

Risk: Усиление rate limit/outage, starvation команд и длительный retry storm.

Example scenario: МойСклад просит подождать 60 секунд, а Merge повторяет каждые 2 секунды до исчерпания attempts.

Recommended solution: Durable `next_attempt_at`, exponential backoff+jitter, чтение rate headers через Egress, bounded attempts и retry/DLQ topic без блокировки consumer poll loop.

Priority: P1

Confidence: High

## [K-04] Invalid SyncRequested коммитится без DLQ/durable incident

Severity: Medium

Category: Kafka

Location: `src/Services/CatalogSyncService/Services/SyncRequestedConsumer.cs`

Lines: `59-83`

Current behavior: Malformed JSON и сообщения с пустыми IDs только логируются, после чего source offset подтверждается.

Problem: Poison message исчезает из operational flow; в отличие от Merge, sync DLQ отсутствует.

Risk: Невозможно восстановить/репроцессить ошибочную команду или связать её с producer regression.

Example scenario: После несовместимого deploy все команды нового формата коммитятся как invalid и теряются.

Recommended solution: Публиковать sanitized DLQ envelope с hash/message metadata до source commit либо сохранять incident в PostgreSQL; мониторить rate.

Priority: P2

Confidence: High

## [K-05] Outbox rows не claim-ятся между instances

Severity: Medium

Category: Kafka

Location: `src/Services/CatalogSyncService/Services/SyncOutboxPublisher.cs`; `src/Services/DuplicatesMergeService/Services/MergeOutboxPublisher.cs`

Lines: Sync `18-65`; Merge `17-58`

Current behavior: Publishers читают одинаковые `PublishedAt == null` rows, отправляют и лишь затем выставляют `PublishedAt`; lock/claim/lease отсутствует.

Problem: При двух replicas одна строка одновременно выбирается несколькими publishers.

Risk: Duplicate commands/events; merge inbox защищает processing одного `MessageId`, но внешние consumers событий должны самостоятельно быть idempotent.

Example scenario: Две replicas выбирают один `MergeRequested`, обе публикуют до SaveChanges.

Recommended solution: `FOR UPDATE SKIP LOCKED`/atomic claim с lease и owner, уникальная consumer inbox, метрики publish lag/attempts.

Priority: P2

Confidence: High

## [K-06] Sync contracts не имеют schema version

Severity: Medium

Category: Kafka

Location: `src/Shared/MsContractor.Contracts/SyncContracts.cs`

Lines: `18-44`

Current behavior: `SyncRequested`, `SyncCompleted`, `SyncFailed` не содержат `SchemaVersion`; MergeRequested версионирован.

Problem: Consumer валидирует только текущую CLR shape и не может явно маршрутизировать/отклонять версии при rolling deploy.

Risk: Добавление/изменение обязательного поля приводит к silent invalid+commit (`K-04`) или неверной интерпретации.

Example scenario: Producer меняет semantics `Mode`, старый consumer принимает JSON, но выполняет другой flow.

Recommended solution: Добавить versioned envelope/Schema Registry policy и compatibility tests до изменения contracts.

Priority: P2

Confidence: High
