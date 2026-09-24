# Генератор связей документов МойСклад

Отдельная developer-утилита на C# / .NET 10. Создаёт в МойСклад графы связанных документов, которые
покрывают **все 45 связей document → document**, которые подтверждены официальной документацией
JSON API 1.2 и реально создаются через API ([DOCUMENT_RELATIONS.md](DOCUMENT_RELATIONS.md)). После создания утилита доказывает каждую
связь повторным `GET`, пишет manifest для MergeVerifier и умеет удалить ровно то, что создала.

Утилита не зависит от сервисов MSContractor, Kafka и БД. Старый генератор
`scripts_for_test_data/` она не использует и не меняет. Решение не входит в `MsContractor.sln`.
Runtime без сторонних NuGet-пакетов, тесты на xUnit.

## Требования

- .NET SDK 10;
- пользователь МойСклад с правами на создание и удаление документов, контрагентов, договоров,
  товаров, услуг и проектов;
- для `retail-flow` — активная точка продаж той же организации (см. ниже).

## Настройка

Credentials задаются только переменными окружения или env-файлом, который игнорируется git.
Переменные окружения имеют приоритет над файлом. Значения никогда не печатаются, не попадают в
manifest и сообщения об ошибках.

| Переменная | Назначение |
|---|---|
| `MS_TOKEN` | Bearer-токен (приоритет над логином) |
| `MS_LOGIN`, `MS_PASSWORD` | Basic Auth; также принимаются `MOYSKLAD_LOGIN` / `MOYSKLAD_PASSWORD` из MergeVerifier |
| `MS_BASE_URL` | по умолчанию `https://api.moysklad.ru/api/remap/1.2/`; только HTTPS и путь JSON API 1.2 |
| `MS_RELTEST_ORGANIZATION_ID` | какую организацию использовать (по умолчанию — первая неархивная) |
| `MS_RELTEST_STORE_ID` | какой склад использовать (по умолчанию — первый неархивный; если складов нет, создаётся тестовый) |
| `MS_RELTEST_RETAIL_STORE_ID` | точка продаж для `retail-flow`; без неё сценарий пропускается |
| `MS_RELTEST_MIN_REQUEST_INTERVAL_MS` | минимальный интервал между запросами, по умолчанию 400 |

Файл: скопируйте `.env.example` в `.env` рядом с утилитой (он в `.gitignore`) или передайте любой
файл через `--env-file`. Имена переменных совпадают со старым генератором, поэтому подойдёт и
`--env-file ../../scripts_for_test_data/.env.test_data`.

## Запуск

Все команды выполняются из этой папки:

```bash
cd scripts/moysklad-document-relations-generator
dotnet build DocumentRelationsGenerator.sln
dotnet test DocumentRelationsGenerator.sln
```

```bash
# сценарии и связи, которые они создают
dotnet run --project DocumentRelationsGenerator -- --list-scenarios

# план без изменений в МойСклад (только GET справочников)
dotnet run --project DocumentRelationsGenerator -- --dry-run --all --seed 7348291

# один сценарий
dotnet run --project DocumentRelationsGenerator -- --scenario sales-return-flow

# несколько сценариев
dotnet run --project DocumentRelationsGenerator -- --scenario sales-flow,purchase-flow

# полный набор на двух контрагентах (main + duplicate для проверки Merge)
dotnet run --project DocumentRelationsGenerator -- --all --counterparties 2

# повтор упавшего сценария с теми же значениями
dotnet run --project DocumentRelationsGenerator -- --scenario purchase-return-flow --seed 7348291 --anchor-date 2026-09-24

# удаление созданного
dotnet run --project DocumentRelationsGenerator -- --cleanup generated-data/20260924-221530-a81f/manifest.json
```

| Параметр | Значение |
|---|---|
| `--all` | все сценарии |
| `--scenario <name>` | один или несколько сценариев (повтор или через запятую) |
| `--list-scenarios` | список сценариев и их связей |
| `--seed <int>` | seed генератора. Без параметра выбирается случайный и печатается: `Seed: 7348291` |
| `--anchor-date <yyyy-MM-dd>` | дата, от которой строятся даты документов (по умолчанию сегодня) |
| `--dry-run` | только чтение: план, зависимости, ожидаемое покрытие. Без credentials — офлайн-план |
| `--cleanup <manifest>` | удалить объекты из manifest; вместе с `--dry-run` — показать план удаления |
| `--counterparties <1-5>` | число тестовых контрагентов; каждый сценарий выполняется для каждого |
| `--output-dir <dir>` | куда писать папки запусков (по умолчанию `generated-data`) |
| `--env-file <path>` | файл с настройками (по умолчанию `./.env`, если есть) |
| `--verbose` | метод, путь и статус каждого HTTP-запроса (без заголовков) |

Коды завершения: `0` — всё создано и подтверждено; `1` — связь или документ не создан/не подтверждён
либо очистка неполная; `2` — ошибка CLI, конфигурации или авторизации; `3` — ошибок нет, но сценарии
пропущены из-за отсутствующих предпосылок (например, точки продаж); `130` — прерывание по Ctrl+C.

## Что создаётся

Справочники аккаунта не создаются и не изменяются: организация, валюты, ставки НДС, сотрудники,
точки продаж. Существующие склад и статья расходов переиспользуются. Тестовые сущности создаются с
префиксом `MSCONTRACTOR-RELTEST-` и run id в имени:

- проект `MSCONTRACTOR-RELTEST-PROJECT-<runId>-001`;
- 5 товаров и 2 услуги (`…-PRODUCT-<runId>-001`, `…-SERVICE-<runId>-001`);
- контрагенты `…-CP-<runId>-001` с двумя расчётными счетами;
- для каждого контрагента договор купли-продажи (`…-CONTRACT-SALES-…`) и, если выбран
  `commission-flow`, договор комиссии (`…-CONTRACT-COMMISSION-…`). `agent` договора — этот
  контрагент, `ownAgent` — выбранная организация;
- склад и статья расходов — только если в аккаунте их нет;
- документы: имя `RT-<runId>-C<контрагент>-<номер сценария>-<номер шага>`,
  `externalCode = mscontractor-reltest-<runId>-c<контрагент>-<сценарий>-<шаг>`.

Run id вида `20260924-221530-a81f` уникален для каждого запуска. Предыдущие запуски не мешают
новому, даже если их не очищали.

## Сценарии

| Сценарий | Граф |
|---|---|
| `sales-flow` | customerorder → invoiceout, demand; demand → factureout; paymentin → customerorder; cashin → demand |
| `sales-invoice-first` | invoiceout → demand; paymentin и cashin → invoiceout; factureout на paymentin |
| `sales-return-flow` | demand → invoiceout, paymentin; salesreturn → demand; loss, paymentout, cashout → salesreturn |
| `factureout-from-cashin` | cashin → demand; factureout на cashin |
| `customerorder-procurement` | purchaseorder на customerorder; cashin → customerorder; cashout → purchaseorder |
| `purchase-flow` | purchaseorder → supply, invoicein; facturein на supply; paymentout → purchaseorder; cashout → supply; facturein на cashout |
| `purchase-invoice-first` | invoicein → supply; paymentout и cashout → invoicein; facturein на paymentout |
| `purchase-return-flow` | supply → invoicein, facturein; purchasereturn → supply; factureout на purchasereturn; paymentin/cashin → purchasereturn; paymentout → supply |
| `commission-flow` | commissionreportin/out (договор комиссии) и по два платежа на каждый |
| `payment-multi-operations` | один paymentin на customerorder и его invoiceout, свой `linkedSum` на каждую операцию |
| `retail-flow` | тестовая смена; retaildemand на customerorder; retailsalesreturn; cashin и paymentin на смену |

Один граф не может вместить всё: у счёта-фактуры ровно одно основание, и на одно основание не
создаётся вторая фактура. Поэтому factureout создаётся отдельно на demand, paymentin, cashin и
purchasereturn, facturein — на supply, paymentout и cashout.

Документ-потомок создаётся только после родителя и получает его настоящий `meta`. Связи со столбцом
Creation = `template` создаются через `PUT /entity/<type>/new` с основанием. Такой шаблон МойСклад
заполняет согласованно: позиции, контрагент, организация, договор, валюта. Затем генератор меняет
только разрешённые поля и отправляет `POST`. Если родитель не создан, его потомки пропускаются
(`[SKIP]`); независимые ветки сценария продолжают выполняться, другие сценарии тоже.

### Розница

Генератор не открывает смену на произвольной точке продаж. Укажите `MS_RELTEST_RETAIL_STORE_ID`
активной точки той же организации. `retail-flow` создаст отдельную смену
`RT-…` (она попадает в manifest и удаляется при cleanup) и документы в ней. Без переменной сценарий
отмечается `SKIPPED`, а `--all` завершается кодом `3`.

## Случайные данные

`TestDataRandomizer` генерирует только значения, допустимые для конкретного типа документа
(`DocumentFieldSupport`, по таблицам атрибутов документации). Неподдерживаемые поля в API не
отправляются. Это проверяет unit-тест; фейковый API в тестах отклоняет неизвестные поля.

- цены в копейках, в трёх диапазонах: 100.00 ₽, 249.90 ₽, 1299 ₽, 8450 ₽;
- quantity 1, 2, 3, 5, 10, у товаров иногда 0.5 / 1.5 / 2.5; discount 0/5/10/15/20
  (у комиссионных отчётов скидки нет);
- НДС — только ставки из справочника `taxrate` аккаунта; `vatEnabled`/`vatIncluded` согласованы с
  позициями; без ставок документы создаются без НДС;
- 1–5 позиций из тестовых товаров, услуги — только в цепочках без возвратов и списаний;
- `moment`: цепочка начинается за 20–200 дней до `--anchor-date` и идёт вперёд
  (заказ ≤ отгрузка ≤ возврат ≤ платёж по возврату), с точностью до минуты;
- `name`, `description`, `externalCode`, `applicable` (у родителей всегда `true`), `shared`,
  `owner`/`group` (текущий сотрудник), `project`, `organizationAccount`;
- `contract` и `agentAccount` чередуются по сценариям и контрагентам, так что в наборе есть все
  четыре комбинации «с/без»;
- `rate`: в `customerorder-procurement` иногда используется не основная валюта, если она есть;
  `organizationAccount` тогда не ставится (ошибка 22004);
- платежи: полная или частичная сумма и `linkedSum`; `paymentPurpose`, `incomingNumber`;
- возвраты: подмножество позиций основания, quantity не больше, чем в основании, цена не меняется;
- `incomingNumber`/`incomingDate`, `deliveryPlannedMoment`, `paymentPlannedMoment`, период
  комиссионного отчёта.

**Воспроизводимость.** Значения зависят только от `--seed`, `--anchor-date`, имени сценария и номера
контрагента. Выбор других сценариев на них не влияет. `--dry-run` печатает ровно те значения, которые
использует реальный запуск. `syncId`, имена и `externalCode` зависят от run id, поэтому повтор с тем же
seed создаёт новые объекты, а не находит старые.

## Проверка

После каждого сценария выполняются `GET` созданных документов:

```text
[OK]   salesreturn -> demand (demand)
[OK]   factureout -> cashin (payments)
[WARN] reverse: demand.returns does not list salesreturn/…
[FAIL] cashout -> supply (operations): not created
```

- связь проверяется на стороне, где она хранится (Source в DOCUMENT_RELATIONS.md): одиночная ссылка,
  массив ссылок или `operations` платежа;
- документированное обратное поле тоже проверяется; расхождение — предупреждение;
- у каждого документа с `agent` проверяется, что это контрагент сценария;
- ответ 200/201 без подтверждения повторным `GET` успехом не считается.

Ошибка выводится так (без заголовков и credentials):

```text
[ERROR] scenario=sales-return-flow type=salesreturn operation=POST create status=412 code=3000 message=…
```

В конце печатаются итоги по сценариям, команда для повтора упавших и coverage report:

```text
====================================
DOCUMENT RELATION COVERAGE
====================================

demand -> customerorder (customerOrder)                        OK
...
------------------------------------
Confirmed relations:  45
Created relations:    45
Verified relations:   45
Failed relations:     0
Coverage:             100%
====================================
```

## Manifest

`generated-data/<runId>/manifest.json` (права 0600) перезаписывается атомарно после каждого созданного
объекта. Поэтому даже прерванный запуск можно очистить. Содержимое:

- `seed`, `anchorDate`, `runId`, `startedAt`, `finishedAt`, `status`, `baseUrl`;
- `counterparties` и `entities`: `created: true` — создано запуском, `false` — переиспользовано
  (никогда не удаляется);
- `scenarios[]`: `documents` (тип, id, имя, href, moment, `dependsOn`, порядковый `sequence`),
  `relations` (`sourceType/sourceId/field/targetType/targetId`, `verified`, `reverseVerified`),
  `errors` (операция, HTTP-статус, код и сообщение МойСклад, тело неудавшегося POST), `warnings`;
- `coverage` — итоговые счётчики и статус каждой связи.

Credentials, заголовки и сырые ответы API в manifest не пишутся.

## Безопасное удаление

```bash
dotnet run --project DocumentRelationsGenerator -- --cleanup generated-data/<runId>/manifest.json --dry-run
dotnet run --project DocumentRelationsGenerator -- --cleanup generated-data/<runId>/manifest.json
```

- удаляются только объекты из manifest с `created: true`; поиска по префиксу нет;
- порядок: документы в обратном порядке создания (потомки раньше родителей), затем тестовые
  сущности (договоры раньше контрагентов);
- перед `DELETE` объект читается: если в `name`/`externalCode` нет run id, он не удаляется;
- 404 — «уже удалён»; 409 — повтор во втором проходе; прочие ошибки попадают в отчёт, работа
  продолжается;
- manifest другого API-хоста не принимается;
- отчёт: `generated-data/<runId>/cleanup-report-<время>.json`.

## Лимиты и повторы

Один запрос за раз, минимум 400 мс между запросами. Интервал подстраивается по `X-RateLimit-Limit` /
`X-Lognex-Retry-TimeInterval`. Ожидание выдерживается по `X-Lognex-Retry-After`, `X-Lognex-Reset`,
`X-RateLimit-Remaining = 0` и `Retry-After`. Логика перенесена из MergeVerifier. `429` повторяется
всегда: запрос не обработан. `GET`, шаблоны и `DELETE` повторяются при 5xx и сетевых сбоях. `POST`
повторяется только с `syncId` (документы, контрагент): по документации повтор с тем же `syncId`
возвращает уже созданный объект. `POST` без `syncId` (товар, договор, проект, комиссионные отчёты)
после таймаута не повторяется. Ошибка помечается как «outcome unknown», в выводе указывается
`externalCode` для ручной проверки.

## Известные ограничения

- `salesreturn.factureOut` и `purchasereturn.factureIn` есть в документации, но через API не
  создаются: `POST`/`PUT` отвечают 200, а повторный `GET` связи не показывает. Такие связи бывают
  только у документов из интерфейса МойСклад. Подробности — в DOCUMENT_RELATIONS.md.
- `retail-flow` на реальном аккаунте не проверен: его точка продаж неактивна.
- Отгрузки создаются без предварительного оприходования. Если в аккаунте запрещена отгрузка
  отсутствующего товара, проведённые demand будут отклонены.
- Связи, которые документированы, но не реализованы, перечислены в DOCUMENT_RELATIONS.md с причинами.

## Проверено на реальном API

2026-09-25, `--all --counterparties 2 --seed 7348291` на аккаунте из
`scripts_for_test_data/.env.test_data`:

- 20 запусков сценариев из 20 прошли (10 сценариев × 2 контрагента); `retail-flow` пропущен
  (точка продаж неактивна), код выхода `3`;
- 39 из 39 нерозничных связей подтверждены встроенным `GET`, обратных расхождений нет;
- отдельный скрипт, не использующий код генератора, перечитал все документы:
  - 88 экземпляров связей и 88 обратных полей подтверждены;
  - `agent` верный у 106 документов, договор своего контрагента у 57;
  - порядок дат верный в 88 парах «родитель → потомок»;
  - у 44 платежей `linkedSum` равен `sum`, а `vatSum ≤ sum`;
  - у 8 позиций возвратов количество не больше, чем в основании, цена та же;
  - проблем 0;
- `--cleanup` на реальном API удалил 66 и 16 объектов двух отладочных запусков без ошибок.

## Структура

```text
scripts/moysklad-document-relations-generator/
├── DocumentRelationsGenerator.sln
├── README.md, DOCUMENT_RELATIONS.md, .env.example, .gitignore
├── generated-data/            # запуски (в .gitignore, кроме .gitkeep)
├── DocumentRelationsGenerator/
│   ├── Cli/                   # аргументы, оркестрация, dry-run, отчёты
│   ├── Configuration/         # env-переменные и env-файл
│   ├── MoySklad/              # HTTP-клиент, лимиты, credentials, meta
│   ├── Relations/             # каталог связей и поддерживаемые поля
│   ├── Scenarios/             # сценарии и топологическая сортировка
│   ├── Randomization/         # TestDataRandomizer, стабильные seed
│   ├── References/            # справочники аккаунта и тестовые сущности
│   ├── Documents/             # payload builders
│   ├── Execution/             # план, запуск сценария, проверка связей
│   ├── Manifest/              # manifest.json
│   ├── Reporting/             # coverage
│   └── Cleanup/               # удаление по manifest
└── DocumentRelationsGenerator.Tests/
```
