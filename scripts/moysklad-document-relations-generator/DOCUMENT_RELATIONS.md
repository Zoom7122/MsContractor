# Связи document → document в JSON API 1.2 МойСклад

Исследовано 2026-09-24 по официальной документации [JSON API 1.2](https://dev.moysklad.ru/doc/api/remap/1.2/).
Сайт документации — SPA: тексты разделов лежат Markdown-строками в webpack-чанках на том же домене
(`https://dev.moysklad.ru/doc/api/remap/1.2/<chunk>.js`). Исследованы именно эти тексты; сторонние
источники, старые версии API и память модели источником истины не были. Имена чанков содержат хеш
содержимого, поэтому ниже указана точная версия каждого раздела.

Таблица «Подтверждённые связи» — единственный источник для `RelationCatalog` генератора:
unit-тест `DocumentRelationsMarkdownTests` сверяет столбец **ID** с каталогом в коде. Coverage report
строится по этим же строкам.

## Как читать таблицу

- **Source** — документ, в котором хранится ссылка (сторона, записываемая при создании связи).
- **Target** — документ, на который ссылается Source.
- **Field** — поле Source. Виды: `ref` — одиночная ссылка `{meta}`; `coll` — массив `[{meta}]`;
  `op` — элемент коллекции `operations` платежа (`{meta, linkedSum}`).
- **Reverse** — документированное поле Target, где связь видна с обратной стороны. `—` означает, что
  обратное поле для этой пары в документации не описано; генератор его не проверяет.
- **Creation** — документированный способ создать связь:
  - `template` — `PUT /entity/<source>/new` с основанием, затем `POST` полученного шаблона
    («Документы → Общие сведения → Шаблоны документов», таблица оснований);
  - `operations` — коллекция `operations` платежа по правилу «Привязка платежей к документам» и
    списку «Разрешенные типы связанных операций» в разделе платежа.
- **After create** — можно ли изменить связь у существующего документа, по тексту документации.

Генератор проверяет связь повторным `GET` на стороне **Source**. Если есть **Reverse**, он делает
`GET` Target и сообщает расхождение как предупреждение.

Общее правило «Привязка документов к документам» (раздел «Документы → Общие сведения») разрешает
передавать ссылку в любое поле секции «Связи с другими документами». На реальном API это верно не
для всех полей: три ссылочных поля принимаются, но не сохраняются. Они вынесены в раздел
«Документированы, но через JSON API не создаются».

## Подтверждённые связи

| ID | Source | Target | Field | Direction | Reverse | Creation | After create | Required conditions | Scenarios | Documentation |
|---|---|---|---|---|---|---|---|---|---|---|
| `demand.customerOrder->customerorder` | demand | customerorder | `customerOrder` ref | demand → customerorder | `customerorder.demands` | template `{customerOrder}` | да, PUT (общее правило привязки) | — | sales-flow, factureout-full | [demand](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/demand#2-otgruzka): «Связи с другими документами», «Шаблон Отгрузки на основе» |
| `demand.invoicesOut->invoiceout` | demand | invoiceout | `invoicesOut` coll | demand → invoiceout | `invoiceout.demands` | template `{invoicesOut:[…]}` | да, PUT (пример привязки 2 в общих сведениях) | — | sales-invoice-first, factureout-full | [demand](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/demand#2-otgruzka): «Шаблон Отгрузки на основе»; [common-info](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/common-info#3-shablony-dokumentov): «Пример привязки 2» |
| `invoiceout.customerOrder->customerorder` | invoiceout | customerorder | `customerOrder` ref | invoiceout → customerorder | `customerorder.invoicesOut` | template `{customerOrder}` | да, PUT (общее правило) | — | sales-flow, payment-multi-operations, factureout-full | [invoiceout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/invoice-out#2-schet-pokupatelyu): «Шаблон Счета покупателю на основе» |
| `invoiceout.demands->demand` | invoiceout | demand | `demands` coll | invoiceout → demand | `demand.invoicesOut` | template `{demands:[…]}` | да, PUT (общее правило) | — | sales-return-flow | [invoiceout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/invoice-out#2-schet-pokupatelyu): «Шаблон Счета покупателю на основе» |
| `salesreturn.demand->demand` | salesreturn | demand | `demand` ref | salesreturn → demand | `demand.returns` | template `{demand}` | **нет**: «Нельзя изменять … demand» | agent, organization и валюта совпадают с demand; позиции — только из demand, quantity ≤ demand, price не меняется | sales-return-flow, factureout-full | [salesreturn](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/sales-return#2-vozvrat-pokupatelya): «Создать Возврат покупателя», «Изменить Возврат покупателя» |
| `loss.salesReturn->salesreturn` | loss | salesreturn | `salesReturn` ref | loss → salesreturn | `salesreturn.losses` | template `{salesReturn}` | да, PUT (общее правило) | у loss нет agent | sales-return-flow | [loss](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/loss#2-spisanie): «Связи с другими документами», «Шаблон Списания на основе» |
| `factureout.demands->demand` | factureout | demand | `demands` coll | factureout → demand | `demand.factureOut` | template `{demands:[…]}` | не описано | основание должно быть указано в единственном экземпляре | sales-flow, factureout-full | [factureout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/factureout#2-schet-faktura-vydannyj): «Создать Счет-фактуру», «Шаблон … на основе» |
| `factureout.payments->paymentin` | factureout | paymentin | `payments` coll | factureout → paymentin | `paymentin.factureOut` | template `{payments:[…]}` | не описано | единственное основание | sales-invoice-first, factureout-full | [factureout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/factureout#2-schet-faktura-vydannyj): «Шаблон … на основе входящего платежа» |
| `factureout.payments->cashin` | factureout | cashin | `payments` coll | factureout → cashin | `cashin.factureOut` | template `{payments:[…]}` | не описано | единственное основание | factureout-from-cashin, factureout-full | [factureout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/factureout#2-schet-faktura-vydannyj): «Создать Счет-фактуру … на основании … приходного ордера»; [cashin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin#2-prihodnyj-order): поле `factureOut` |
| `factureout.returns->purchasereturn` | factureout | purchasereturn | `returns` coll | factureout → purchasereturn | `purchasereturn.factureOut` | template `{returns:[…]}` | не описано | единственное основание; `returns` — только возвраты поставщикам | purchase-return-flow, factureout-full | [factureout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/factureout#2-schet-faktura-vydannyj): «Шаблон … на основе возврата поставщику» |
| `purchaseorder.customerOrders->customerorder` | purchaseorder | customerorder | `customerOrders` coll | purchaseorder → customerorder | `customerorder.purchaseOrders` | template `{customerOrders:[…]}` | да, PUT (общее правило) | в шаблоне нет agent — поставщика нужно указать самому | customerorder-procurement | [purchaseorder](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/purchaseOrder#2-zakaz-postavshiku): «Шаблон Заказа поставщику на основе» |
| `supply.purchaseOrder->purchaseorder` | supply | purchaseorder | `purchaseOrder` ref | supply → purchaseorder | `purchaseorder.supplies` | template `{purchaseOrder}` | да, PUT (общее правило) | — | purchase-flow | [supply](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/supply#2-priemka): «Шаблон Приемки на основе» |
| `supply.invoicesIn->invoicein` | supply | invoicein | `invoicesIn` coll | supply → invoicein | `invoicein.supplies` | template `{invoicesIn:[…]}` | да, PUT (общее правило) | — | purchase-invoice-first | [supply](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/supply#2-priemka): «Шаблон Приемки на основе счета поставщика» |
| `invoicein.purchaseOrder->purchaseorder` | invoicein | purchaseorder | `purchaseOrder` ref | invoicein → purchaseorder | `purchaseorder.invoicesIn` | template `{purchaseOrder}` | да, PUT (общее правило) | — | purchase-flow | [invoicein](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/invoice-in#2-schet-postavshika): «Шаблон Счета поставщика на основе» |
| `invoicein.supplies->supply` | invoicein | supply | `supplies` coll | invoicein → supply | `supply.invoicesIn` | template `{supplies:[…]}` | да, PUT (общее правило) | — | purchase-return-flow | [invoicein](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/invoice-in#2-schet-postavshika): «Шаблон Счета поставщика на основе приемки» |
| `purchasereturn.supply->supply` | purchasereturn | supply | `supply` ref | purchasereturn → supply | `supply.returns` | template `{supply}` | **нет**: «Нельзя изменять … supply» | agent, organization и валюта совпадают с supply; позиции — только из supply, quantity ≤ supply | purchase-return-flow, factureout-full | [purchasereturn](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/purchase-return#2-vozvrat-postavshiku): «Создать Возврат поставщику», «Изменить Возврат поставщику» |
| `facturein.supplies->supply` | facturein | supply | `supplies` coll | facturein → supply | `supply.factureIn` | template `{supplies:[…]}` | не описано | единственное основание; `incomingNumber` и `incomingDate` передаются явно | purchase-flow, purchase-return-flow | [facturein](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/facturein#2-schet-faktura-poluchennyj): «Создать Счет-фактуру», «Шаблон … на основе приемки» |
| `facturein.payments->paymentout` | facturein | paymentout | `payments` coll | facturein → paymentout | `paymentout.factureIn` | template `{payments:[…]}` | не описано | единственное основание; `incomingNumber`/`incomingDate` передаются явно | purchase-invoice-first | [facturein](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/facturein#2-schet-faktura-poluchennyj): «Шаблон … на основе исходящего платежа» |
| `facturein.payments->cashout` | facturein | cashout | `payments` coll | facturein → cashout | `cashout.factureIn` | template `{payments:[…]}` | не описано | единственное основание; `incomingNumber`/`incomingDate` передаются явно. В разделе «Создать Счет-фактуру» названы только приёмка и исходящий платёж; основание-cashout подтверждено на реальном API (см. «Проверка на реальном API») | purchase-flow | [cashout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashout#2-rashodnyj-order): поле `factureIn` «…с которым связан этот платеж»; [facturein](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/facturein#2-schet-faktura-poluchennyj): `payments`; common-info: «Привязка платежей к документам» (расходный ордер — один из 4 типов платежей) |
| `paymentin.operations->customerorder` | paymentin | customerorder | `operations` op | paymentin → customerorder | `customerorder.payments` | operations (template `{operations:[…]}`) | да, PUT `operations` | тип из списка разрешённых операций | sales-flow, payment-multi-operations, factureout-full | [paymentin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-in#2-vhodyashij-platezh): «Разрешенные типы связанных операций», «Шаблон Входящего платежа на основе» |
| `paymentin.operations->demand` | paymentin | demand | `operations` op | paymentin → demand | `demand.payments` | operations (template) | да, PUT `operations` | — | sales-return-flow, factureout-full | [paymentin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-in#2-vhodyashij-platezh) |
| `paymentin.operations->invoiceout` | paymentin | invoiceout | `operations` op | paymentin → invoiceout | `invoiceout.payments` (только чтение) | operations (template) | да, PUT `operations` | — | sales-invoice-first, payment-multi-operations, factureout-full | [paymentin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-in#2-vhodyashij-platezh) |
| `paymentin.operations->purchasereturn` | paymentin | purchasereturn | `operations` op | paymentin → purchasereturn | `purchasereturn.payments` | operations (template) | да, PUT `operations` | — | purchase-return-flow, factureout-full | [paymentin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-in#2-vhodyashij-platezh) |
| `paymentin.operations->commissionreportin` | paymentin | commissionreportin | `operations` op | paymentin → commissionreportin | `commissionreportin.payments` | operations (template) | да, PUT `operations` (пример привязки платежа 1) | — | commission-flow, factureout-full | [paymentin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-in#2-vhodyashij-platezh); common-info: «Пример привязки платежа 1» |
| `paymentin.operations->retailshift` | paymentin | retailshift | `operations` op | paymentin → retailshift | — | operations (POST) | да, PUT `operations` | для retailshift нет шаблона платежа, коллекция передаётся в POST | retail-flow | [paymentin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-in#2-vhodyashij-platezh): «Смена (retailShift)» в списке разрешённых операций |
| `cashin.operations->customerorder` | cashin | customerorder | `operations` op | cashin → customerorder | `customerorder.payments` | operations (template) | да, PUT `operations` (пример привязки платежа 2) | — | customerorder-procurement, factureout-full | [cashin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin#2-prihodnyj-order); common-info: «Пример привязки платежа 2» |
| `cashin.operations->demand` | cashin | demand | `operations` op | cashin → demand | `demand.payments` | operations (template) | да, PUT `operations` | — | sales-flow, factureout-from-cashin, factureout-full | [cashin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin#2-prihodnyj-order) |
| `cashin.operations->invoiceout` | cashin | invoiceout | `operations` op | cashin → invoiceout | `invoiceout.payments` (только чтение) | operations (template) | да, PUT `operations` | — | sales-invoice-first, factureout-full | [cashin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin#2-prihodnyj-order) |
| `cashin.operations->purchasereturn` | cashin | purchasereturn | `operations` op | cashin → purchasereturn | `purchasereturn.payments` | operations (template) | да, PUT `operations` | — | purchase-return-flow, factureout-full | [cashin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin#2-prihodnyj-order) |
| `cashin.operations->commissionreportin` | cashin | commissionreportin | `operations` op | cashin → commissionreportin | `commissionreportin.payments` | operations (template) | да, PUT `operations` | — | commission-flow, factureout-full | [cashin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin#2-prihodnyj-order) |
| `cashin.operations->retailshift` | cashin | retailshift | `operations` op | cashin → retailshift | — | operations (POST) | да, PUT `operations` | шаблона нет, коллекция передаётся в POST | retail-flow | [cashin](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashin#2-prihodnyj-order): «Смена (retailShift)» |
| `paymentout.operations->purchaseorder` | paymentout | purchaseorder | `operations` op | paymentout → purchaseorder | `purchaseorder.payments` | operations (template) | да, PUT `operations` | `expenseItem` обязателен | purchase-flow | [paymentout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-out#2-ishodyashij-platezh): «Разрешенные типы связанных операций», «Шаблон Исходящего платежа на основе» |
| `paymentout.operations->supply` | paymentout | supply | `operations` op | paymentout → supply | `supply.payments` | operations (template) | да, PUT `operations` | `expenseItem` обязателен | purchase-return-flow | [paymentout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-out#2-ishodyashij-platezh) |
| `paymentout.operations->invoicein` | paymentout | invoicein | `operations` op | paymentout → invoicein | `invoicein.payments` (только чтение) | operations (template) | да, PUT `operations` | `expenseItem` обязателен | purchase-invoice-first | [paymentout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-out#2-ishodyashij-platezh) |
| `paymentout.operations->salesreturn` | paymentout | salesreturn | `operations` op | paymentout → salesreturn | `salesreturn.payments` | operations (template) | да, PUT `operations` | `expenseItem` обязателен | sales-return-flow | [paymentout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-out#2-ishodyashij-platezh) |
| `paymentout.operations->commissionreportout` | paymentout | commissionreportout | `operations` op | paymentout → commissionreportout | `commissionreportout.payments` | operations (template) | да, PUT `operations` | `expenseItem` обязателен | commission-flow | [paymentout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/payment-out#2-ishodyashij-platezh) |
| `cashout.operations->purchaseorder` | cashout | purchaseorder | `operations` op | cashout → purchaseorder | `purchaseorder.payments` | operations (template) | да, PUT `operations` | `expenseItem` обязателен | customerorder-procurement | [cashout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashout#2-rashodnyj-order): «Разрешенные типы связанных операций», «Шаблон Расходного ордера на основе» |
| `cashout.operations->supply` | cashout | supply | `operations` op | cashout → supply | `supply.payments` | operations (template) | да, PUT `operations` | `expenseItem` обязателен | purchase-flow | [cashout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashout#2-rashodnyj-order) |
| `cashout.operations->invoicein` | cashout | invoicein | `operations` op | cashout → invoicein | `invoicein.payments` (только чтение) | operations (template) | да, PUT `operations` | `expenseItem` обязателен | purchase-invoice-first | [cashout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashout#2-rashodnyj-order) |
| `cashout.operations->salesreturn` | cashout | salesreturn | `operations` op | cashout → salesreturn | `salesreturn.payments` | operations (template) | да, PUT `operations` | `expenseItem` обязателен | sales-return-flow | [cashout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashout#2-rashodnyj-order) |
| `cashout.operations->commissionreportout` | cashout | commissionreportout | `operations` op | cashout → commissionreportout | `commissionreportout.payments` | operations (template) | да, PUT `operations` | `expenseItem` обязателен | commission-flow | [cashout](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/cashout#2-rashodnyj-order) |
| `retaildemand.retailShift->retailshift` | retaildemand | retailshift | `retailShift` ref | retaildemand → retailshift | — | template `{retailShift}` | не описано | смена активна; moment продажи позже moment смены | retail-flow | [retaildemand](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retaildemand#2-roznichnaya-prodazha): «Создать Розничную продажу», «Шаблон Розничной продажи» |
| `retaildemand.customerOrder->customerorder` | retaildemand | customerorder | `customerOrder` ref | retaildemand → customerorder | — | template `{retailShift, customerOrder}` | да, PUT (общее правило) | шаблон всегда требует retailShift | retail-flow | [retaildemand](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retaildemand#2-roznichnaya-prodazha): «Шаблон Розничной продажи» (на основе заказа покупателя) |
| `retailsalesreturn.demand->retaildemand` | retailsalesreturn | retaildemand | `demand` ref | retailsalesreturn → retaildemand | — | template `{demand}` | **нет**: «Нельзя изменять … demand» | agent, organization и валюта совпадают с retaildemand; позиции только из retaildemand, quantity ≤ | retail-flow | [retailsalesreturn](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retail-sales-return#2-roznichnyj-vozvrat): «Создать Розничный возврат», «Шаблон Розничного возврата на основе» |
| `retailsalesreturn.retailShift->retailshift` | retailsalesreturn | retailshift | `retailShift` ref | retailsalesreturn → retailshift | — | template (смена переносится из retaildemand) | не описано | обязательное поле при создании | retail-flow | [retailsalesreturn](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/retail-sales-return#2-roznichnyj-vozvrat): «Создать Розничный возврат» |

Итого: **45** подтверждённых и создаваемых через API связей. Генератор создаёт каждую хотя бы один раз.

## Взаимоисключения и ограничения, повлиявшие на сценарии

1. **У счёта-фактуры ровно одно основание.** «Документ-основание должен быть указан в единственном
   экземпляре» (разделы «Создать Счет-фактуру» у factureout и facturein). Поэтому отдельные экземпляры:
   `factureout-from-demand`, `factureout-from-paymentin`, `factureout-from-cashin`,
   `factureout-from-purchasereturn`, `facturein-from-supply`, `facturein-from-paymentout`,
   `facturein-from-cashout`.
2. **Одна фактура на одно основание.** Документация прямо этого не формулирует. Старый генератор
   (`scripts_for_test_data/README.md`) зафиксировал эмпирически: вторую factureout на тот же demand
   МойСклад отклоняет, вторая facturein на тот же supply даёт ошибку `35001`. Сценарии построены так,
   что у каждого основания не больше одной фактуры. Это соблюдение наблюдаемого ограничения, а не
   связь, взятая из эксперимента.
3. **Основание возврата неизменяемо** (`salesreturn.demand`, `purchasereturn.supply`,
   `retailsalesreturn.demand`). Связь задаётся только при создании, позиции берутся из шаблона.
   Количество уменьшается не больше, чем в основании; цена не меняется.
4. **Связь со счётом-фактурой пишется только со стороны фактуры** (`demands`, `supplies`,
   `payments`, `returns`). Поля `factureOut`/`factureIn` у отгрузок, платежей и возвратов — обратная
   сторона. Если передать их в `POST`/`PUT` самого документа, МойСклад отвечает 200, но связь не
   сохраняет (проверено GET, см. ниже).
5. **Платёж:** `noClosingDocs = true` нельзя передать вместе с непустым `operations` (paymentout,
   cashout). Генератор `noClosingDocs` не передаёт.
6. **Commission reports:** `contract` обязателен и должен быть договором комиссии (`contractType =
   Commission`) с тем же контрагентом.
7. **Розница:** продажа создаётся только в активной смене и позже её `moment`. Генератор не открывает
   смену на произвольной точке продаж: точку нужно явно указать в `MS_RELTEST_RETAIL_STORE_ID`, иначе
   `retail-flow` пропускается с причиной.

## Документированы, но через JSON API не создаются

Поля есть в секциях «Связи с другими документами», общее правило привязки их допускает. Но на реальном
API (2026-09-24, аккаунт из `scripts_for_test_data/.env.test_data`) связь не сохраняется. Такие связи
появляются только у документов, созданных в интерфейсе МойСклад. Генератор их не создаёт, в coverage
они не входят.

| Поле | Что сделано | Результат GET |
|---|---|---|
| `salesreturn.factureOut → factureout` | `POST salesreturn` с `factureOut`; затем `PUT salesreturn` с `factureOut` | оба ответа 200, поля `factureOut` в документе нет |
| `purchasereturn.factureIn → facturein` | `POST purchasereturn` с `factureIn`; затем `PUT purchasereturn` с `factureIn` | оба ответа 200, поля `factureIn` в документе нет |
| `cashout.factureIn → facturein` (запись со стороны cashout) | `POST cashout` с `factureIn` | ответ 200, поля нет. Та же связь создаётся со стороны фактуры: `facturein.payments->cashout` (в таблице выше) |
| `factureout.returns → salesreturn` (обратный путь для `salesreturn.factureOut`) | `PUT /entity/factureout/new` с `returns: [salesreturn]` | HTTP 400, код `1060` «Некорректный тип данных в meta сущности, поле type» |

У facturein нет поля для возврата поставщику, поэтому обратного пути для `purchasereturn.factureIn` нет.

## Проверка на реальном API

2026-09-25 все сценарии, кроме `retail-flow`, созданы на реальном аккаунте на двух контрагентах
(`--all --counterparties 2`), и каждая связь прочитана повторным `GET`:

- все 39 нерозничных связей (88 экземпляров) подтверждены на стороне Source встроенной проверкой и
  отдельным скриптом;
- обратные поля (`demands`, `returns`, `payments`, `factureOut`, `factureIn`, `supplies`,
  `invoicesIn`, `invoicesOut`, `purchaseOrders`, `losses`) совпали во всех 88 случаях;
- `facturein.payments->cashout` видна с обеих сторон: `facturein.payments` и `cashout.factureIn`;
- 6 розничных связей не проверены: единственная точка продаж аккаунта неактивна (`active: false`),
  а генератор не меняет настройки аккаунта;
- найдена и исправлена ошибка генератора: частичный платёж с `vatSum` из шаблона (НДС всего
  основания) отклонялся с кодом `3007` «vatSum не может быть больше sum». Теперь НДС платежа
  считается по доле НДС в основании.

## Документы, исследованные без связей document → document

| Тип | Есть agent | Создание через API | Связи | Решение |
|---|---|---|---|---|
| `counterpartyadjustment` | да (контрагент или сотрудник) | да | секции «Связи с другими документами» нет | связей для генерации нет |
| `commissionreportin` / `commissionreportout` | да | да | только обратная `payments` (со стороны платежей) | создаются в `commission-flow` как цели платежей |
| `retireorder` | да (`agent`) | да, при тарифной опции «Маркировка» и маркированном товаре | `supportingTransaction` — enum, а не ссылка на документ | связей нет, в генератор не входит |

## Подтверждены документацией, но не реализованы

| Связь | Почему не реализована |
|---|---|
| `prepayment.customerOrder → customerorder`, `customerorder.prepayments`, `prepaymentreturn.prepayment → prepayment`, `prepayment.returns` | [prepayment](https://dev.moysklad.ru/doc/api/remap/1.2/#/documents/prepayment) и prepaymentreturn в JSON API 1.2 доступны только для чтения и удаления (Получить, Удалить, позиции). Создания нет — сгенерировать связь нельзя. |
| `bonustransaction.parentDocument → retaildemand` | [Бонусная операция](https://dev.moysklad.ru/doc/api/remap/1.2/#/dictionaries/bonus-operation#2-bonusnaya-operaciya) создаётся и имеет `agent`, но требует существующую бонусную программу (настройка аккаунта). Допустимые типы `parentDocument` не перечислены: есть только пример с retaildemand. MSContractor этот тип не обрабатывает. Не добавлено. |
| `salesreturn` на основании `retaildemand` | Упомянуто только в общей таблице шаблонов. Раздел salesreturn описывает `demand` как «Ссылка на отгрузку» и шаблон только на основании отгрузки; ключ тела шаблона для retaildemand не документирован. Не реализовано как факт. |
| `retailsalesreturn` на основании `retailshift` (без документа-продажи) | Документировано, но это возврат без основания. Связь с розничной сменой уже покрыта `retailsalesreturn.retailShift->retailshift`. |
| `move.customerOrder`, `move.demand`, `move.supply`, `move.internalOrder`, `customerorder.moves` | У перемещения нет agent. Оно не ссылается на пересоздаваемые MSContractor документы, а связи с customerorder/demand/supply сохраняются при PutAgent (ID не меняется). |
| `purchaseorder.internalOrder`, `internalorder.purchaseOrders`, `internalorder.moves` | Внутренний заказ без agent; не участвует в merge MSContractor. |
| `*.productionTasks`, `supply.productionTask` | Производственные задания без agent, требуют тарифа «Производство»; MSContractor их не обрабатывает. |
| `loss.inventory → inventory`, `enter` на основании `inventory` | Инвентаризация и оприходование без agent; не связаны с документами контрагента. |
| `retaildrawercashin` / `retaildrawercashout` → `retailshift` | Документы внесения/выплаты денег без agent. |

## Не подтверждено (в генератор не входит)

| Утверждение | Результат проверки |
|---|---|
| `factureout.returns` принимает `salesreturn` | Нет: поле описано как «возвраты поставщикам», шаблон — «на основе возврата поставщику». |
| Шаблоны платежей на основании `retailshift` | В таблице шаблонов у paymentin/cashin смены нет; связь со сменой создаётся `POST` с `operations`. |

## Поля документов для рандомизации

Поля взяты из таблиц «Атрибуты сущности»: `y` — записываемое, `REQ` — «Необходимо при создании»,
`RO` — только для чтения, `-` — поля нет. Генератор записывает только поля `y`/`REQ` и только в
документы, где поле есть (`DocumentFieldSupport`).

| Документ | contract | agentAccount | organizationAccount | store | project | vatEnabled | positions | syncId | прочее |
|---|---|---|---|---|---|---|---|---|---|
| customerorder | y | y | y | y | y | y | y (discount, vat, reserve) | y | deliveryPlannedMoment |
| demand | y | y | y | REQ | y | y | y (discount, vat) | y | — |
| invoiceout | y | y | y | y | y | y | y (discount, vat) | y | paymentPlannedMoment |
| invoicein | y | y | y | y | y | y | y (discount, vat) | y | incomingNumber, incomingDate, paymentPlannedMoment |
| supply | y | y | y | REQ | y | y | y (discount, vat) | y | incomingNumber, incomingDate |
| purchaseorder | y | y | y | y | y | y | y (discount, vat) | y | deliveryPlannedMoment |
| paymentin | y | y | y | - | y | - | - | y | paymentPurpose, incomingNumber, incomingDate |
| paymentout | y | y | y | - | y | - | - | y | expenseItem (REQ), paymentPurpose |
| cashin | y | - | - | - | y | - | - | y | paymentPurpose |
| cashout | y | - | - | - | y | - | - | y | expenseItem (REQ), paymentPurpose |
| commissionreportin | REQ (Commission) | y | y | - | y | y | y (vat, без discount) | RO | commissionPeriodStart/End (REQ) |
| commissionreportout | REQ (Commission) | y | y | - | y | y | y (vat, без discount) | RO | name (REQ), commissionPeriodStart/End (REQ); shared — RO |
| salesreturn | y | y | y | REQ | y | y | из основания | y | — |
| purchasereturn | y | y | y | REQ | y | y | из основания (discount RO) | y | — |
| factureout | y | - | - | - | - | - | - | y | paymentPurpose (только при основании-платеже) |
| facturein | y | - | - | - | - | - | - | y | incomingNumber, incomingDate |
| loss | - | - | - | REQ | y | - | из основания | y | у loss нет agent |
| retaildemand | - | y | y | REQ | - | y | из основания | y | cashSum, noCashSum; organization — RO |
| retailsalesreturn | y | y | y | REQ | - | y | из основания | y | — |
| retailshift | - | - | - | авто | - | - | - | y | organization, retailStore (REQ) |

Денежные значения (`price`, `sum`, `linkedSum`) — в копейках. `moment` хранится с точностью до
минуты («Формат даты и времени»). Ставки НДС позиций берутся из справочника `taxrate` аккаунта.

## Версии исходных разделов

Все файлы — `https://dev.moysklad.ru/doc/api/remap/1.2/<имя>`:

```text
md-documents-_common_info-md.6900e959b37784ed39b8.js
md-_general-md.8705b08aaa357f5545fe.js
md-documents-_customerOrder-md.783f2f80deb398a084a0.js
md-documents-_demand-md.b1b7f0852a3120d7e32d.js
md-documents-_invoice_out-md.38165f4a8528bc591155.js
md-documents-_invoice_in-md.9d366eceffa4a688d38e.js
md-documents-_supply-md.a1733334f481f3489453.js
md-documents-_purchaseOrder-md.6efa4694d4ca035aa699.js
md-documents-_payment_in-md.b18bed089029a7dec034.js
md-documents-_payment_out-md.98beca02f0846f87d0ce.js
md-documents-_cashin-md.d5b694573905414e2621.js
md-documents-_cashout-md.e1af054169f906d69239.js
md-documents-_retaildemand-md.99e287e37b5628e10ea6.js
md-documents-_retailshift-md.d5ef9795746eee8f48f8.js
md-documents-_counterpartyadjustment-md.86c795e6a5eddce49193.js
md-documents-_commissionreportin-md.176f952491c90547af9e.js
md-documents-_commissionreportout-md.8055ec85a0705439f12a.js
md-documents-_sales_return-md.5b2ea539f6b6aa0c9069.js
md-documents-_purchase_return-md.cae833d24ccb80ab490f.js
md-documents-_retail_sales_return-md.2f1bb3e1ee9ea1f6e8a6.js
md-documents-_factureout-md.c0e7e875c0888f0e65e8.js
md-documents-_facturein-md.3ab138942650087e7c9a.js
md-documents-_retireorder-md.f760b6293ba12cce91d2.js
md-documents-_prepayment-md.f3ef28e3edb90c3a9e3d.js
md-documents-_prepayment_return-md.e1e6e226d9be3e52fb89.js
md-documents-_loss-md.e896c6c93c2644f373ab.js
md-dictionaries-_bonus_operation-md.ca280c1dbfd70df52f83.js
md-dictionaries-_contract-md.22ec026c217d16df2ee7.js
md-dictionaries-_taxrate-md.fb8db223b15fd488bd4d.js
```
