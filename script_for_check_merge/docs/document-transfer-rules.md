# Правила переноса документов

Исследовано 2026-09-20 по актуальному содержимому официальной [JSON API 1.2](https://dev.moysklad.ru/doc/api/remap/1.2/).
Сайт загружает Markdown из JavaScript chunks; исследованы тексты разделов из этих chunks на том же официальном домене. Никакие изменяющие API запросы не выполнялись.
Все 20 имен из задания совпадают с именами API; customerOrder/purchaseOrder в адресах документации имеют другой регистр, endpoints — lowercase.
Режим описывает ожидаемую идентичность результата, не является обещанием успешности произвольного PUT при любых связях/статусах. Verifier всегда выполняет только GET.

| Entity type | GET supported | PUT entity supported | agent writable | Transfer mode | Notes |
|---|---|---|---|---|---|
| [customerorder](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/customerOrder) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [demand](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/demand) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [purchaseorder](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/purchaseOrder) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [supply](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/supply) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [paymentin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-in) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [paymentout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-out) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [cashin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [cashout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashout) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [retaildemand](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retaildemand) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [invoiceout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/invoice-out) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [invoicein](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/invoice-in) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [counterpartyadjustment](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/counterpartyadjustment) | да | да | да, с условиями | PutAgent | Раздел изменения разрешает все поля, кроме помеченных Только для чтения; agent присутствует в таблице атрибутов, пометки Только для чтения нет. Это совместное основание для PutAgent, а не только наличие PUT endpoint. |
| [commissionreportin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/commissionreportin) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [commissionreportout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/commissionreportout) | да | да | да, с условиями | PutAgent | Раздел изменения прямо обсуждает обновление agent вместе с agentAccount; это подтверждение изменения существующего документа. |
| [salesreturn](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/sales-return) | да | да | нет | Recreate | Раздел изменения явно запрещает изменение agent. Для возврата с основанием требуется совпадение контрагента с основанием. |
| [purchasereturn](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/purchase-return) | да | да | нет | Recreate | Раздел изменения явно запрещает изменение agent; при создании возврата с основанием контрагент должен совпадать с основанием. purchasereturn содержит противоречивую общую фразу; приоритет отдан конкретному запрету. |
| [retailsalesreturn](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retail-sales-return) | да | да | нет | Recreate | Раздел изменения явно запрещает изменение agent. Для возврата с основанием требуется совпадение контрагента с основанием. |
| [factureout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/factureout) | да | да | не подтверждено для общего случая | Unsupported | GET и PUT описаны, но пример PUT меняет только name. Нет уверенного подтверждения изменения именно agent; режим не угадывается. |
| [facturein](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/facturein) | да | да | не подтверждено для общего случая | Unsupported | GET и PUT описаны, но пример PUT меняет только name. Нет уверенного подтверждения изменения именно agent; режим не угадывается. |
| [retireorder](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retireorder) | да | да | не подтверждено для общего случая | Unsupported | Изменение ограничено documentState CREATED, CHECKED_NOT_OK, PROCESSING_ERROR; общей гарантии переноса нет. agent не имеет документированного оператора фильтрации. Endpoint не сканируется целиком; тип всегда отмечается Unsupported. |

## Ограничения и полнота

`factureout`, `facturein` запрашиваются с фильтром agent и фиксируются в snapshot, но сравнение не объявляется успешным.
`retireorder` не загружается без документированного agent-фильтра. Он явно присутствует в capture coverage как Unsupported, даже если неизвестно, есть ли такие документы.
Поэтому текущая строгая проверка всего заданного реестра **не выдаст полный PASS**: результат будет exit 1 с Unsupported. Это осознанное выполнение требования не угадывать неописанные правила. Убрать это ограничение можно только после дополнения доказательств и правил, а не CLI-переключателем скрытия типов.

## Нормализация

- Для PutAgent из корня убираются `id` (вынесен в stableDocumentId), `agent` (вынесен в sourceCounterpartyId), `accountId`, `meta`, `href`, `uuidHref`, `updated`. `created` сохраняется.
- Для Recreate дополнительно удаляется `created`. ID не сохраняется даже в stableDocumentId. `externalCode`, `name`, `code` и неизвестные бизнес-поля сохраняются: изменение автоматически сгенерированного кода тоже диагностируется.
- agentAccount, contract, owner, group, shared, state, organization, store, project, currency/rate, суммы, дополнительные поля и все остальные возвращенные бизнес-поля сохраняются. Смена agent не оправдывает потерю этих данных.
- Нормализаторы имеют отдельные профили типов и обрабатывают фактически возвращенную schema. Общего whitelist полей нет, новые поля не теряются.
- Meta ссылки преобразуются в тип и ключ ресурса. ID связанных бизнес-сущностей сохраняются, служебные URL представления/метаданных исключаются. Запросы не запрашивают expand бизнес-ссылок. Если сервер вернул дополнительные поля внутри ссылки, они также сохраняются.
- Связь с PutAgent документом сохраняет ID (`salesreturn.demand`, `purchasereturn.supply`, `retailsalesreturn.demand` (ссылка на retaildemand)). Для ссылки на Recreate документ используется SHA-256 полного нормализованного связанного документа. Такой документ при необходимости загружается GET отдельно; он не добавляется в проверяемый scope. Циклическая или неразрешимая связь — ERROR, а не молчаливое исключение.
- Все позиции загружаются с пагинацией. У `commissionreportin` отдельно загружаются `positions` и `returnToCommissionerPositions`. У финансовых документов, counterpartyadjustment и счетов-фактур `/positions` не вызывается.
- У позиции удаляются только собственные `id`, `accountId`, `meta`; ссылки assortment/product/service и остальные поля (включая quantity, price, reserve, cost, trackingCodes, things) сохраняются.
- Позиции и документированные коллекции связей сравниваются как мультимножества: сортировка canonical JSON сохраняет повторы. Attributes сортируются как набор типизированных дополнительных полей. Для прочих массивов порядок сохраняется, включая вложенные trackingCodes/things: без доказательства они не переставляются.
- Вложенные коллекции MetaArray загружаются полностью (включая метаданные прикрепленных файлов). Бинарное содержимое файлов не скачивается; это ограничение покрытия явно относится к файлам, не позициям. У файлов исключаются meta/created и временные ссылки download/miniature/tiny; идентификатор файла из meta.href сохраняется как fileIdentity. Согласно [документации файлов](https://dev.moysklad.ru/doc/api/remap/1.2/#/dictionaries/files), одинаковым filename/content соответствует одинаковый ID. Одинаковые файлы с одинаковыми ID допустимы, multiplicity сохраняется. Это ID файла, не пересоздаваемого документа. Имя, размер и остальные метаданные также сохраняются.

## HTTP

[Общие сведения: аутентификация, сжатие, метаданные и обработка ошибок](https://dev.moysklad.ru/doc/api/remap/1.2/#/general):
UTF-8 Basic Auth, `Accept: application/json;charset=utf-8`, `Accept-Encoding: gzip`, один HttpClient, один одновременный запрос, минимум 400 мс между запросами. МойСклад возвращает HTTP 400/code 1062 для `Accept: application/json` без charset.
Учитываются `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-Lognex-Retry-TimeInterval`, `X-Lognex-Reset`, `X-Lognex-Retry-After` (Lognex значения в миллисекундах), HTTP Retry-After.
GET повторяется не более 5 попыток при 429/502/503/504, сетевой ошибке и timeout. 401/403 не повторяются. Редиректы запрещены, ссылки проверяются на тот же origin и API path до отправки credentials.
Пагинация проверяет size/offset/limit, nextHref, отсутствие повторных страниц/ID и изменение размера коллекции. Частичные результаты не сохраняются как завершенный snapshot.

## Проверяемые источники

- `customerorder`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/customerOrder); SHA-256 текста раздела `990e50b153bd64ad0b423710f88fc435564878aa95485b792759415f0563c972`.
- `demand`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/demand); SHA-256 текста раздела `bf97d4dc387a33dbf83d0dda6502c85c4e001642c8794984a290ae29d738eed9`.
- `purchaseorder`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/purchaseOrder); SHA-256 текста раздела `764b7ccb19ed7d6890e353427bf053531e42484bc3faf8832441feab6e43b12e`.
- `supply`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/supply); SHA-256 текста раздела `bc8f7ddb8ad94c6ab47612c9e69b3be60b5341c81a103a1ae6b41b4b8249a6d1`.
- `paymentin`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-in); SHA-256 текста раздела `2040d69f5661fe319c1651e5a7e453e4a5f89ee3ce98580794385510defc951a`.
- `paymentout`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-out); SHA-256 текста раздела `05ffe0fe32c0230e7c76ccad93b449e0915984a410f1605011eef6feae8517a0`.
- `cashin`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin); SHA-256 текста раздела `79db2dd1ce24e2c18892930657ee3f8ea1872a3db5960779b65d2f2fe748138a`.
- `cashout`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashout); SHA-256 текста раздела `9de7a93ce5640e594b89cf55f57cff8fc363fce8a44e05b90de713c02d634d08`.
- `retaildemand`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retaildemand); SHA-256 текста раздела `cbc444bac3431bd4ed02f599ebebcefbb35f15cb2da2c080bbe6e7c0384e500b`.
- `invoiceout`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/invoice-out); SHA-256 текста раздела `fdf814cc5e47bc85bb6ce53b1030008a05dcc2496210d43bffb3f67ecff4503f`.
- `invoicein`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/invoice-in); SHA-256 текста раздела `9c07b919105b20041b826a01753e48ca8ce4ca5966b0714e6cf2b468d85254f7`.
- `counterpartyadjustment`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/counterpartyadjustment); SHA-256 текста раздела `0d64b3ce95ed1bff2cc8d3e80c6841d16725001a5b5268f76828c4c021df200e`.
- `commissionreportin`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/commissionreportin); SHA-256 текста раздела `7a39d5885dfe6b1732baf6bd3be073a5c2a5f0da423d7290988b7ac1ceda9c1d`.
- `commissionreportout`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/commissionreportout); SHA-256 текста раздела `729e1bd22742adeed5a6fe430f62115850807db062da64d2584560397f07ba84`.
- `salesreturn`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/sales-return); SHA-256 текста раздела `655da2bf576f92c7d0166030436d54584b7f6def262518c4a2dc62a18e0190a0`.
- `purchasereturn`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/purchase-return); SHA-256 текста раздела `8b6d55ff9fc886d6b647249f900b21c8af7e19434620d35dececb63c408920fb`.
- `retailsalesreturn`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retail-sales-return); SHA-256 текста раздела `0240969732e8b09599c041f1c859b44f9e10326dcb092136a6c6b3abc6919464`.
- `factureout`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/factureout); SHA-256 текста раздела `149205fe4bef4a8c5982d1eb07f6dd43c29de8a793cb31d552e63a58be7e01a1`.
- `facturein`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/facturein); SHA-256 текста раздела `5bc641d8899caaf08e5a4d0eae942b027d7e370751bf936d522a9be48e82bdb0`.
- `retireorder`: [атрибуты, получение и изменение](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retireorder); SHA-256 текста раздела `6b16d73ac1db948c3cc588cb0cde6448340801049bd9f8060a243c487879731e`.
