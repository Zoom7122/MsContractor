# Security audit

Critical security findings `C-01`—`C-04` описаны в [01-critical.md](01-critical.md).

## [S-01] Ошибка Redis после deactivation оставляет действующую Gateway session

Severity: High

Category: Security

Location: `src/Services/VendorService/Services/VendorInstallationService.cs`; `src/Services/Gateway.Bff/Services/GatewaySessionReader.cs`

Lines: Vendor `155-180`; Gateway `19-42`

Current behavior: Installation/token сначала необратимо переводятся в inactive в PostgreSQL, затем вызывается Redis revoke. Gateway при чтении session проверяет только Redis JSON и не перепроверяет installation status.

Problem: DB commit и session invalidation не образуют согласованную операцию/retryable job.

Risk: После uninstall/suspend пользователь продолжает вызывать account-scoped Gateway API до TTL session.

Example scenario: Redis временно недоступен на строке revoke; callback отвечает ошибкой, но повтор может стать idempotent DB replay, а старая Redis session сохраняется.

Recommended solution: Сделать revocation durable и повторяемой (outbox/job), в Gateway учитывать account revocation/version либо проверять активность; покрыть fail-after-DB-commit тестом.

Priority: P1

Confidence: High

## [S-02] Cookie-authenticated POST endpoints не имеют явной CSRF-защиты

Severity: Medium

Category: Security

Location: `src/Services/Gateway.Bff/Controllers/SyncController.cs`; `src/Services/VendorService/Controllers/MoyskladSessionController.cs`

Lines: Sync `14-26,32-46`; Session `114-125,155-164`

Current behavior: Session cookie имеет `SameSite=None`; sync и logout принимают POST без anti-forgery token или Origin/Referer enforcement.

Problem: Browser прикладывает cookie к cross-site запросам. JSON merge сложнее вызвать из-за preflight, но body-less sync/logout доступны через обычную форму/навигационный POST.

Risk: Принудительный full sync, расход лимитов/ресурсов или logout жертвы.

Example scenario: Внешняя страница автоматически отправляет form POST на `/api/sync` в iframe-origin.

Recommended solution: Добавить CSRF token либо строгую проверку Origin + Fetch Metadata для state-changing API; ограничить CORS и тестировать browser flow.

Priority: P2

Confidence: High

## [S-03] Access token передаётся между Vendor и Egress по plaintext HTTP

Severity: High

Category: Security

Location: `src/Services/VendorService/Controllers/InternalInstallationController.cs`; `src/Services/MoySkladEgressService/Services/VendorTokenClient.cs`; `docker-compose.yml`

Lines: controller `18-56`; client `21-34,59-71`; Compose `13-14`

Current behavior: Token корректно зашифрован at rest, но расшифровывается и возвращается JSON-ответом Egress по `http://vendor-service:8080` с shared API key.

Problem: Сервисная сеть не аутентифицирована криптографически и транспорт не шифруется.

Risk: Компрометированный container/network observer получает долгоживущий MoySklad access token.

Example scenario: Контейнер с доступом к bridge network перехватывает internal response либо вызывает token endpoint с утёкшим ключом.

Recommended solution: Применить mTLS/workload identity и узкую network policy; предпочтительно выполнять token-bound вызов через защищённый broker/sidecar, не раскрывая token вызывающему сервису.

Priority: P1

Confidence: High

## [S-04] Общий Compose env нарушает least privilege для секретов

Severity: High

Category: Security

Location: `docker-compose.yml`

Lines: `3-18,133-134,165-166,198-200,224-225,252-253,281-282,308-309`

Current behavior: `ConnectionStrings__Postgres`, Redis, Kafka и `InternalApi__Key` передаются каждому backend, включая Notification/Audit, которые их не используют.

Problem: Компрометация любого наименее защищённого контейнера раскрывает привилегии других bounded contexts.

Risk: Lateral movement к БД, Kafka, Redis и internal APIs.

Example scenario: Уязвимость в placeholder AuditService позволяет прочитать environment и получить DB password/internal key.

Recommended solution: Выдавать каждому сервису отдельные DB users, service credentials и только нужные env; использовать mounted secrets, rotation и deny-by-default network policies.

Priority: P1

Confidence: High

## [S-05] Duplicate preview отдаёт frontend полный RawJson МойСклада

Severity: High

Category: Security

Location: `src/Services/DuplicatesMergeService/Services/DuplicatePreviewService.cs`; `src/Shared/MsContractor.Contracts/DuplicatePreviewContracts.cs`

Lines: service `36-41,80-94`; contract `1-20`

Current behavior: Для карточек, которым UI нужны name/email/phone/description, API дополнительно десериализует и возвращает весь сохранённый raw counterparty JSON.

Problem: Data minimization отсутствует; raw upstream schema может содержать реквизиты и дополнительные поля, не предназначенные для iframe.

Risk: Избыточное раскрытие tenant data через browser/devtools и увеличение blast radius XSS/client logging.

Example scenario: МойСклад добавляет чувствительное поле в JSON counterparty; backend автоматически начинает отдавать его frontend без contract review.

Recommended solution: Удалить RawJson из публичного DTO, использовать explicit allowlist полей и добавить contract snapshot/security test.

Priority: P1

Confidence: High

## [S-06] Swagger и internal OpenAPI доступны независимо от environment

Severity: Medium

Category: Security

Location: `src/Services/Gateway.Bff/Program.cs`; `src/Services/Gateway.Bff/appsettings.json`; `src/Services/DuplicatesMergeService/Program.cs`

Lines: Gateway Program `53-63`; appsettings `37-66`; Duplicates Program `51-56`

Current behavior: Gateway всегда публикует Swagger UI и проксирует OpenAPI всех внутренних сервисов; environment/auth guard отсутствует.

Problem: Внешний клиент получает точные internal routes/contracts/error models и dev surface.

Risk: Упрощение reconnaissance и эксплуатации `C-03`; лишняя production attack surface.

Example scenario: Атакующий открывает `/_openapi/moysklad-egress/openapi/v1.json` и получает формат internal archive/discovery запросов.

Recommended solution: Отключить UI/proxy routes в Production либо защитить отдельной admin authentication/network policy.

Priority: P2

Confidence: High
