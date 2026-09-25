# МойСклад integration audit

Сверка выполнена с [JSON API 1.2](https://dev.moysklad.ru/doc/api/remap/1.2/) и [Vendor API 1.0](https://dev.moysklad.ru/doc/api/vendor/1.0/). Подтверждено: максимальный `limit` коллекции и batch create/update/delete — 1000; массовый запрос ограничен 5 минутами; rate-limit ответы содержат специальные headers.

## [M-01] Merge archive batch не ограничен лимитом 1000

Severity: High

Category: MoySklad

Location: `src/Services/DuplicatesMergeService/Services/MergeJobCreator.cs`; `src/Services/MoySkladEgressService/Services/MoySkladCounterpartyGateway.cs`

Lines: creator `97-106,143-162`; gateway `180-203,259-280`

Current behavior: Request validation не задаёт maximum duplicates; все IDs отправляются одним `POST entity/counterparty`.

Problem: JSON API ограничивает list create/update/delete 1000 объектами и рекомендует уменьшать batch при достижении 5-minute timeout. [Официальная документация](https://dev.moysklad.ru/doc/api/remap/1.2/)

Risk: Большая группа гарантированно отклоняется/таймаутится, затем весь archive step повторяется.

Example scenario: Merge 1200 дублей выполняет main PUT, после чего archive POST >1000 fails и job остаётся partial/failed.

Recommended solution: Валидировать product limit либо chunk ≤1000 (операционно меньше), фиксировать состояние каждого chunk/duplicate и не повторять уже подтверждённые.

Priority: P1

Confidence: High

## [M-02] Partial failure batch archive применяется ко всем duplicates

Severity: High

Category: MoySklad

Location: `src/Services/DuplicatesMergeService/Services/MergeProcessor.cs`; `tests/MsContractor.Sync.Tests/MergeProcessorTests.cs`

Lines: processor `94-148`; tests `64-85`

Current behavior: Любая exception/malformed/incomplete batch response вызывает `HandleFailureAsync` для каждой operation. Parser требует массив только из успешных полных counterparty DTO.

Problem: Индивидуальный успех/ошибка из batch не сопоставляется с конкретным duplicate; уже применённые upstream изменения могут считаться failed и повторяться.

Risk: Неверный `PartiallyCompleted`, повторные archives, локальный catalog расходится с МойСкладом.

Example scenario: Первый duplicate архивирован, второй rejected; response не проходит общий parser, и обе operations помечаются failed.

Recommended solution: Парсить mixed batch results по position/id, сохранять terminal state каждого элемента, затем reconciliation GET для ambiguous timeout/invalid response.

Priority: P1

Confidence: High

## [M-03] Rate-limit hints не используются при retry merge

Severity: High

Category: MoySklad

Location: `src/Services/DuplicatesMergeService/Consumers/MergeRequestedConsumer.cs`; `src/Services/MoySkladEgressService/RateLimiting/MoySkladRateLimiter.cs`

Current behavior: Merge повторяет операцию через фиксированные 2 секунды, не получая `Retry-After` от Egress; ключ состояния лимита в Redis живёт `X-Lognex-Retry-TimeInterval` и может истечь раньше `Retry-After`.

Problem: Повтор до разрешённого времени, дополнительные 429 и блокировка merge behind sync.

Risk: Исчерпание попыток merge на 429.

Example scenario: Upstream просит подождать дольше интервала, ключ истекает, и запросы уходят раньше срока.

Recommended solution: Передавать `Retry-After` из Egress в Merge, держать `blocked_until` не меньше `Retry-After`; тестировать clock/skew/malformed headers.

Priority: P1

Confidence: High

## [M-04] Discovery filter не ограничивает длину URL

Severity: Medium

Category: MoySklad

Location: `src/Services/MoySkladEgressService/Services/MoySkladDocumentGateway.cs`; `src/Services/MoySkladEgressService/Services/MoySkladDocumentDiscoveryService.cs`

Lines: gateway `39-52`; discovery validation `24-55`

Current behavior: Для каждого входного counterparty в один URL добавляется `agent=<full href>`, затем весь filter URL-encode-ится. Maximum count/request URI length не задан.

Problem: Хотя repeated equality filter допустим, URI растёт линейно и может превысить limits proxy/server раньше business validation.

Risk: 414/400 или client-side Uri exception для большой выборки; discovery не возвращает partial result, но операция полностью теряется.

Example scenario: Сотни IDs создают URL размером в десятки килобайт.

Recommended solution: Установить подтверждённый safe maximum либо chunk filter с точной dedup/count reconciliation; добавить boundary tests длины URI.

Priority: P2

Confidence: High
