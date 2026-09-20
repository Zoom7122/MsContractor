# MergeVerifier

Самостоятельный диагностический инструмент на C# / .NET 10. Получает документы напрямую из МойСклад JSON API 1.2 через **Basic Auth**, сохраняет BEFORE/AFTER и проверяет конечное состояние после ручного объединения контрагентов в другом приложении.

Только GET. Инструмент не запускает Merge, не читает данные самих контрагентов, не подключается к сервисам/DTO/БД основного проекта. Runtime не требует сторонних NuGet-пакетов; тестовый проект использует xUnit.

**Ограничение текущего реестра:** `factureout`, `facturein`, `retireorder` имеют режим `Unsupported`. Для первых двух нет достаточного подтверждения изменения `agent`, у последнего нет общей гарантии переноса и документированного фильтра `agent`. Строгая проверка всех 20 типов поэтому возвращает **exit 1**, даже если все поддерживаемые документы совпали. Отчёт показывает результаты каждого типа и причину неполноты; скрытого полного PASS нет. Источники и обоснования: [document-transfer-rules.md](docs/document-transfer-rules.md).

## Запуск

Требуется .NET SDK 10. Все следующие команды выполняются из этой папки:

```bash
cd "MS_service/script_for_check_merge"
dotnet build MergeVerifier.sln
dotnet test MergeVerifier.sln
```

Задайте credentials **только переменными окружения** текущего процесса:

```bash
export MOYSKLAD_LOGIN="your-login"
export MOYSKLAD_PASSWORD="your-password"
```

Значения выше — заполнители. Для ввода пароля без записи значения в историю команд можно использовать в Bash/Zsh:

```bash
printf 'MoySklad password: '
stty -echo
read -r MOYSKLAD_PASSWORD
stty echo
printf '\n'
export MOYSKLAD_PASSWORD
```

`.env` автоматически не загружается. Логин/пароль не принимаются из CLI или JSON. При отсутствии любой обязательной переменной — exit 2 до HTTP-запросов. При 401/403 запрос не повторяется, программа предлагает проверить переменные, не печатая их значения или тело ответа API.

Опциональная конфигурация:

| Переменная | Значение по умолчанию |
|---|---|
| `MOYSKLAD_BASE_URL` | `https://api.moysklad.ru/api/remap/1.2/` |
| `MERGE_VERIFIER_SNAPSHOT_DIR` | `snapshots` относительно текущего каталога |

Переопределение base URL допускает HTTPS endpoint JSON API 1.2; серверные ссылки должны оставаться на том же origin и внутри `entity/`. Редиректы не выполняются.

### 1. BEFORE

Замените UUID примера на main и duplicates своего аккаунта:

```bash
dotnet run --project MergeVerifier -- capture \
  --main "11111111-1111-1111-1111-111111111111" \
  --duplicates "22222222-2222-2222-2222-222222222222,55555555-5555-5555-5555-555555555555"
```

UUID проверяются; повторные duplicates удаляются; пустой UUID, пустой список и main внутри duplicates запрещены. Main означает обязательный UUID аргумента: отдельный GET контрагента не выполняется, его карточка не сохраняется.

Программа покажет количество документов каждого типа для main и каждого duplicate. Получаются все страницы списка, отдельные документы, все страницы позиций и вложенных MetaArray. Вторая коллекция `commissionreportin.returnToCommissionerPositions` загружается отдельно. Финансовые документы и корректировки не имеют `/positions`.

Результат:

```text
snapshots/20260921-001500_11111111_a1b2c3/before.json
```

Суффикс предотвращает коллизии одновременных capture. BEFORE создаётся после успешного получения всего поддерживаемого dataset и не перезаписывается. Неподтвержденное покрытие явно записано в `coverage`. Успешный capture возвращает 0; это означает сохранение снимка, а не успешную проверку Merge.

### 2. Merge

Выполните объединение вручную в основном приложении и дождитесь его завершения. Snapshot не транзакционный: время начала/окончания сохраняется. Во время capture и verify не редактируйте выбранные документы; посторонние изменения между снимками будут отражены в отчёте.

### 3. AFTER и сверка

Возьмите точный путь `before.json`, напечатанный capture:

```bash
dotnet run --project MergeVerifier -- verify \
  --snapshot "snapshots/20260921-001500_11111111_a1b2c3/before.json"
```

Main и duplicates читаются из BEFORE. Рядом записываются `after.json` и `report.json`; `before.json` не изменяется. Повторный verify заменяет AFTER/report атомарно. Если получение данных не завершилось, частичный AFTER не сохраняется, report содержит `FetchError`, `afterCaptureCompleted: false`, exit 2. Старый AFTER при этом может оставаться на диске и не относится к неуспешному запуску. Ошибки до начала capture (например, неверный snapshot или credentials) сообщаются в stderr с exit 2.

| Код | Значение |
|---|---|
| 0 | Capture завершён / verification passed |
| 1 | Missing, Changed, Unexpected, StillOnDuplicate или Unsupported |
| 2 | Ошибка CLI, конфигурации, авторизации, сети, JSON, snapshot, записи либо неполное получение данных |

## Правила проверки

| Режим | Документы |
|---|---|
| PutAgent | customerorder, demand, purchaseorder, supply, paymentin, paymentout, cashin, cashout, retaildemand, invoiceout, invoicein, counterpartyadjustment, commissionreportin, commissionreportout |
| Recreate | salesreturn, purchasereturn, retailsalesreturn |
| Unsupported | factureout, facturein, retireorder |

- BEFORE включает документы main и всех duplicates. Основной AFTER — документы main; документы оставшиеся на duplicates дают `StillOnDuplicate` и не засчитываются как Matched.
- PutAgent сопоставляется по `stableDocumentId`; сравниваются нормализованные бизнес-данные. Смена ID даёт Missing + Unexpected; смена поля — Changed с JSON Pointer в `differences`.
- Recreate сопоставляется по canonical semantic content, используя SHA-256 для отчёта. Количество одинаковых документов сохраняется. Изменение данных даёт Missing + Unexpected; если осталась ровно одна семантическая разновидность с каждой стороны, дополнительно показывается diff возможной пары. Эта подсказка не устанавливает идентичность документов.
- `agent` исключён из semantic data и отдельно хранится как проверенный по API `sourceCounterpartyId`; AFTER обязан принадлежать main. Проверяется фактическая ссылка в документе, а не только значение фильтра запроса.
- У PutAgent игнорируются корневые `id` (вынесен отдельно), `agent` (проверяется отдельно), `accountId`, `meta`, `href`, `uuidHref`, `updated`. У Recreate дополнительно игнорируется `created`, и `stableDocumentId` отсутствует.
- `externalCode`, `code`, `agentAccount`, договор, организация, склад, валюта/курс, даты документа, суммы, дополнительные поля, состояние, владелец и неизвестные бизнес-поля сохраняются. Normalizer не исключает их ради PASS.
- Позиции нормализуются отдельно: убираются только собственные `id`, `accountId`, `meta`; сохраняются ссылки assortment и все бизнес-поля. Сортировка canonical JSON делает коллекцию мультимножеством: `[A,B,A] == [A,A,B]`, но `[A,A] != [A]`.
- JSON properties сортируются; числа канонизируются без потери точности. Неподтвержденный порядок вложенных массивов сохраняется. Документированные коллекции связей и attributes сортируются с сохранением повторов.
- Бизнес-ссылки на другие сущности сохраняют идентичность. Ссылки на Recreate документы заменяются семантическим hash связанного документа без old/new ID mapping; неразрешимая/циклическая связь вызывает ERROR.
- У файлов проверяются метаданные, число повторов и документированная идентичность filename/content (`fileIdentity`); бинарное содержимое не скачивается. Нормализованный snapshot не содержит сырой API payload или credentials.

Подробные ограничения API для каждого типа и ссылки на официальные разделы находятся в [таблице исследования](docs/document-transfer-rules.md).

## Примеры и структура

Синтетические примеры: [BEFORE](examples/before.json), [AFTER](examples/after.json), [JSON report](examples/report.json), [console report](examples/console-report.txt). Это иллюстрации формата, не снимки реального аккаунта; у возврата в примере изменилось количество позиции, поэтому он отображается как Missing + Unexpected с diff.

```text
script_for_check_merge/
├── MergeVerifier.sln
├── README.md
├── docs/document-transfer-rules.md
├── examples/{before.json,after.json,report.json,console-report.txt}
├── MergeVerifier/
│   ├── MergeVerifier.csproj
│   ├── Program.cs
│   ├── Cli/
│   ├── Configuration/
│   ├── MoySklad/
│   ├── Capture/
│   ├── Documents/
│   ├── Models/
│   ├── Normalization/
│   ├── Comparison/
│   ├── Storage/
│   └── Reporting/
└── MergeVerifier.Tests/
```

`snapshots/`, `.env*`, `bin/`, `obj/`, `TestResults/` исключены локальным `.gitignore`. На Unix файлы snapshot/report создаются с правами 0600. Решение не включено в основной `MsContractor.sln`.

Тесты используют поддельный HTTP handler, без настоящего API: PutAgent/Recreate, multiplicity документов/позиций, бизнес-ссылки, точность canonical JSON, Basic Auth UTF-8, отсутствие credentials в выводе, retry/пагинация, ошибки snapshot, неизменность BEFORE и полный capture/verify цикл с файлами отчёта.
