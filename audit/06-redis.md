# Redis audit

## [R-01] Distributed rate limiter неатомарен, без лимита параллельности и приоритета

Severity: High

Category: Redis

Location: `src/Services/MoySkladEgressService/RateLimiting/MoySkladRateLimiter.cs`

Current behavior: `WaitAsync` читает последний наблюдённый остаток и пропускает запрос, если он больше резерва; резервирования, декремента, семафора параллельности и приоритета merge нет.

Problem: Несколько Egress instances, sync и merge одновременно видят один и тот же остаток и превышают budget; full sync может занять все upstream slots перед merge writes.

Risk: 429 storms, нарушение приоритета critical writes, непредсказуемая производительность при horizontal scaling.

Example scenario: Два full sync одновременно отправляют страницы; merge PUT проходит без reservation и получает 429.

Recommended solution: Атомарный Redis limiter с account/user/global buckets, bounded concurrency, `blocked_until`, TTL/fencing и отдельной приоритетной очередью write; задокументировать fallback при Redis failure.

Priority: P0

Confidence: High

## [R-02] Обычная активность через Gateway не продлевает sliding session

Severity: Medium

Category: Redis

Location: `src/Services/Gateway.Bff/Services/GatewaySessionReader.cs`; `src/Services/VendorService/Services/VendorSessionStore.cs`

Lines: Gateway `19-42`; Vendor store `68-106`

Current behavior: Vendor `/me` обновляет TTL и `LastActivityAt`, но Gateway controllers делают только `StringGet`; cookie также не переиздаётся.

Problem: Заявленная sliding expiration зависит от периодического вызова `/me`, а не от реальной BFF activity.

Risk: Активный пользователь внезапно теряет session через 8 часов либо frontend вынужден создавать лишний keepalive.

Example scenario: Пользователь весь день запускает preview/merge, но `/me` не вызывается — Redis key истекает.

Recommended solution: Централизовать touch с throttling в session middleware/store и синхронно обновлять cookie expiry, не выполняя запись на каждый запрос.

Priority: P2

Confidence: High

## [R-03] Account session revocation не атомарна с созданием session

Severity: Medium

Category: Redis

Location: `src/Services/VendorService/Services/VendorSessionStore.cs`

Lines: create `42-65`; revoke `135-148`

Current behavior: Revoke читает set members, затем отдельной командой удаляет вычисленные keys. Concurrent Create может добавить token после чтения или переcоздать account set.

Problem: Нет revocation generation/lock/Lua atomicity между create и revoke.

Risk: Одна session переживает uninstall/suspend race.

Example scenario: Create добавляет новый token сразу после `SetMembersAsync`; revoke удаляет старый snapshot и account key, новый session key остаётся.

Recommended solution: Account revocation version в session payload + atomic Lua/transaction/fencing; запрет create для inactive installation и race integration test.

Priority: P2

Confidence: High
