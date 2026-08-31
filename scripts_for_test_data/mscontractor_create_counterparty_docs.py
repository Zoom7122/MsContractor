#!/usr/bin/env python3
import argparse
import json
import os
import shlex
import sys
import time
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any, Dict, Optional, Set, Tuple

import requests


def load_test_data_env() -> None:
    """Load .env.test_data located next to this script without extra dependencies."""
    env_path = Path(__file__).with_name(".env.test_data")
    if not env_path.is_file():
        return

    for line_number, raw_line in enumerate(
        env_path.read_text(encoding="utf-8-sig").splitlines(), start=1
    ):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[7:].lstrip()
        if "=" not in line:
            raise ValueError(f"Некорректная строка {line_number} в {env_path.name}")

        key, raw_value = line.split("=", 1)
        key = key.strip()
        if not key:
            raise ValueError(f"Пустое имя переменной в строке {line_number} в {env_path.name}")
        try:
            parsed_values = shlex.split(raw_value, comments=True)
        except ValueError as exc:
            raise ValueError(
                f"Некорректное значение в строке {line_number} в {env_path.name}"
            ) from exc
        value = " ".join(parsed_values)
        os.environ.setdefault(key, value)


load_test_data_env()

#Запуск
#"python3 mscontractor_create_counterparty_docs.py"

# Сколько КА создать по умолчанию. Можно переопределить через MS_COUNTERPARTY_COUNT.
DEFAULT_COUNTERPARTY_COUNT = 2


# Значения MS_COUNTERPARTY_COUNT, MS_COUNTERPARTY_NAME, MS_COUNTERPARTY_PHONE,
# MS_WRITE_REPORT_JSON и MS_DOCUMENT_TYPES задаются в окружении.
# Типы документов можно передать через MS_DOCUMENT_TYPES, через запятую или пробел:
# MS_DOCUMENT_TYPES=customerorder,demand,invoiceout
# Аргументы --docs, --stats и --all имеют более высокий приоритет.

BASE_URL = os.getenv("MS_BASE_URL", "https://api.moysklad.ru/api/remap/1.2").rstrip("/")
MS_LOGIN = os.getenv("MS_LOGIN", "")
MS_PASSWORD = os.getenv("MS_PASSWORD", "")
COUNTERPARTY_NAME = os.getenv("MS_COUNTERPARTY_NAME", "MSContractor DOCS TEST")
COUNTERPARTY_PHONE = os.getenv("MS_COUNTERPARTY_PHONE", "+79990000000")
REQUEST_DELAY_SECONDS = float(os.getenv("MS_REQUEST_DELAY_SECONDS", "0.25"))
MS_ERROR_LOG_DIR = Path(__file__).with_name("Logs_MS_test_data")

# Документы, которые участвуют в показателе "Сумма продаж" контрагента:
# Отгрузка + Розничная продажа + Полученный отчет комиссионера
# минус Возврат покупателя + Розничный возврат.
STAT_DOCUMENT_TYPES = {
    "demand",
    "retaildemand",
    "commissionreportin",
    "salesreturn",
    "retailsalesreturn",
}

ALL_DOCUMENT_TYPES = {
    "customerorder",
    "demand",
    "invoiceout",
    "invoicein",
    "supply",
    "purchaseorder",
    "cashin",
    "cashout",
    "paymentin",
    "paymentout",
    "salesreturn",
    "purchasereturn",
    "counterpartyadjustment",
    "facturein",
    "factureout",
    "commissionreportin",
    "commissionreportout",
    "retaildemand",
    "retailsalesreturn",
}

PRODUCT_DOCUMENT_TYPES = {
    "customerorder",
    "demand",
    "invoiceout",
    "invoicein",
    "supply",
    "purchaseorder",
    "salesreturn",
    "purchasereturn",
    "retaildemand",
    "retailsalesreturn",
}

STORE_DOCUMENT_TYPES = {
    "demand",
    "supply",
    "salesreturn",
    "purchasereturn",
    "retaildemand",
    "retailsalesreturn",
}

RETAIL_DOCUMENT_TYPES = {
    "retaildemand",
    "retailsalesreturn",
}

EXPENSE_ITEM_DOCUMENT_TYPES = {
    "cashout",
    "paymentout",
}

COMMISSION_REPORT_DOCUMENT_TYPES = {
    "commissionreportin",
    "commissionreportout",
}

DOCUMENT_TITLES = {
    "customerorder": "Заказ покупателя",
    "demand": "Отгрузка",
    "invoiceout": "Счёт покупателю",
    "invoicein": "Счёт поставщика",
    "supply": "Приёмка",
    "purchaseorder": "Заказ поставщику",
    "cashin": "Приходный ордер",
    "cashout": "Расходный ордер",
    "paymentin": "Входящий платёж",
    "paymentout": "Исходящий платёж",
    "salesreturn": "Возврат покупателя",
    "purchasereturn": "Возврат поставщику",
    "counterpartyadjustment": "Корректировка взаиморасчётов",
    "facturein": "Полученный счёт-фактура",
    "factureout": "Выданный счёт-фактура",
    "commissionreportin": "Полученный отчёт комиссионера",
    "commissionreportout": "Выданный отчёт комиссионера",
    "retaildemand": "Розничная продажа",
    "retailsalesreturn": "Возврат розничной продажи",
}


class ApiError(Exception):
    def __init__(self, method: str, path: str, status: int, body: str):
        super().__init__(f"{method} {path} failed with status={status}")
        self.method = method
        self.path = path
        self.status = status
        self.body = body


session = requests.Session()
session.headers.update({
    "Accept": "application/json;charset=utf-8",
    "Content-Type": "application/json;charset=utf-8",
    "Accept-Encoding": "gzip",
    "Lognex-Pretty-Print-JSON": "true",
    "User-Agent": "mscontractor-counterparty-docs-test-script/2.0",
})


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Создаёт тестового контрагента и выбранные документы в МойСклад."
    )

    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--stats",
        action="store_true",
        help=(
            "Создать документы, участвующие в CRM-показателе 'Сумма продаж': "
            "demand, retaildemand, commissionreportin, salesreturn, retailsalesreturn. "
            "Если MS_DOCUMENT_TYPES не задан, это режим по умолчанию."
        ),
    )
    mode.add_argument(
        "--all",
        action="store_true",
        help="Создать все поддерживаемые этим скриптом документы.",
    )
    mode.add_argument(
        "--docs",
        nargs="+",
        choices=sorted(ALL_DOCUMENT_TYPES),
        metavar="TYPE",
        help="Создать только перечисленные типы документов.",
    )

    parser.add_argument(
        "--draft",
        action="store_true",
        help=(
            "Создавать документы непроведёнными (applicable=false). "
            "В этом режиме они не будут учитываться в показателях контрагента."
        ),
    )
    parser.add_argument(
        "--list-documents",
        action="store_true",
        help="Показать доступные типы документов и завершить работу.",
    )

    return parser.parse_args()


def counterparty_count() -> int:
    """Return the number of counterparties requested through the environment."""
    raw_value = os.getenv("MS_COUNTERPARTY_COUNT", str(DEFAULT_COUNTERPARTY_COUNT))

    try:
        count = int(raw_value)
    except (TypeError, ValueError) as exc:
        raise ValueError(
            "MS_COUNTERPARTY_COUNT должен быть целым числом не меньше 1"
        ) from exc

    if count < 1:
        raise ValueError("MS_COUNTERPARTY_COUNT должен быть целым числом не меньше 1")
    return count


def write_report_json() -> bool:
    """Return whether the JSON run report should be written."""
    raw_value = os.getenv("MS_WRITE_REPORT_JSON", "true").strip().lower()
    if raw_value in {"1", "true", "yes", "on"}:
        return True
    if raw_value in {"0", "false", "no", "off"}:
        return False
    raise ValueError(
        "MS_WRITE_REPORT_JSON должен быть true/false (также допустимы 1/0, yes/no)"
    )


def selected_document_types(args: argparse.Namespace) -> Set[str]:
    if args.all:
        return set(ALL_DOCUMENT_TYPES)
    if args.docs:
        return set(args.docs)
    if args.stats:
        return set(STAT_DOCUMENT_TYPES)

    raw_types = os.getenv("MS_DOCUMENT_TYPES")
    if raw_types is not None:
        if not raw_types.strip():
            return set()

        document_types = {
            document_type.lower()
            for document_type in raw_types.replace(",", " ").split()
        }
        unknown_types = document_types - ALL_DOCUMENT_TYPES
        if unknown_types:
            raise ValueError(
                "MS_DOCUMENT_TYPES содержит неподдерживаемые типы: "
                f"{', '.join(sorted(unknown_types))}"
            )
        if not document_types:
            raise ValueError("MS_DOCUMENT_TYPES не должен быть пустым")
        return document_types

    return set(STAT_DOCUMENT_TYPES)


def print_document_types() -> None:
    print("Доступные типы документов:")
    for doc_type in sorted(ALL_DOCUMENT_TYPES):
        stats_mark = " [CRM stats]" if doc_type in STAT_DOCUMENT_TYPES else ""
        print(f"  {doc_type:<26} {DOCUMENT_TITLES[doc_type]}{stats_mark}")


def validate_credentials() -> None:
    if not MS_LOGIN or not MS_PASSWORD:
        print("Ошибка: задай MS_LOGIN и MS_PASSWORD через переменные окружения.")
        print("Пример:")
        print("export MS_LOGIN='admin@example.com'")
        print("export MS_PASSWORD='password'")
        sys.exit(1)

    session.auth = (MS_LOGIN, MS_PASSWORD)


def sleep_limit() -> None:
    time.sleep(REQUEST_DELAY_SECONDS)


def api(method: str, path: str, payload: Optional[Dict[str, Any]] = None) -> Any:
    url = f"{BASE_URL}{path}"
    sleep_limit()

    response = session.request(
        method=method,
        url=url,
        json=payload,
        timeout=60,
    )

    if 200 <= response.status_code < 300:
        if response.text.strip():
            return response.json()
        return None

    raise ApiError(method, path, response.status_code, response.text)


def try_api(method: str, path: str, payload: Optional[Dict[str, Any]] = None) -> Tuple[bool, Any]:
    try:
        return True, api(method, path, payload)
    except ApiError as e:
        return False, {
            "method": e.method,
            "path": e.path,
            "status": e.status,
            "body": e.body,
        }
    except Exception as e:
        return False, {
            "method": method,
            "path": path,
            "status": None,
            "body": str(e),
        }


def meta(entity: Dict[str, Any]) -> Dict[str, Any]:
    return {"meta": entity["meta"]}


def get_first(entity_type: str) -> Optional[Dict[str, Any]]:
    ok, data = try_api("GET", f"/entity/{entity_type}?limit=1")
    if not ok:
        return None

    rows = data.get("rows", [])
    return rows[0] if rows else None


def create_entity(entity_type: str, payload: Dict[str, Any]) -> Dict[str, Any]:
    return api("POST", f"/entity/{entity_type}", payload)


def ensure_organization() -> Dict[str, Any]:
    org = get_first("organization")
    if not org:
        raise RuntimeError("В аккаунте не найдена организация. Создай организацию в МойСклад.")
    return org


def ensure_store(run_id: str) -> Dict[str, Any]:
    store = get_first("store")
    if store:
        return store

    print("Склад не найден. Пробую создать тестовый склад...")
    return create_entity("store", {
        "name": f"MSContractor Test Store {run_id}",
        "description": "Создано скриптом для теста документов mscontractor",
    })


def ensure_product(run_id: str) -> Dict[str, Any]:
    print("Создаю тестовый товар...")
    return create_entity("product", {
        "name": f"MSContractor Test Product {run_id}",
        "code": f"MSCTEST-PRODUCT-{run_id}",
        "externalCode": f"mscontractor-product-{run_id}",
        "description": "Тестовый товар для документов mscontractor",
    })


def ensure_expense_item(run_id: str) -> Dict[str, Any]:
    expense_item = get_first("expenseitem")
    if expense_item:
        return expense_item

    print("Статья расходов не найдена. Создаю тестовую статью расходов...")
    return create_entity("expenseitem", {
        "name": f"MSContractor Test Expense {run_id}",
        "description": "Создано скриптом для расходных документов mscontractor",
    })


def try_ensure_retail_store(
    run_id: str,
    org: Dict[str, Any],
    store: Dict[str, Any],
) -> Optional[Dict[str, Any]]:
    ok, data = try_api("GET", "/entity/retailstore?limit=100")
    if not ok:
        print("Не удалось получить список розничных точек.")
        print(data.get("body", ""))
        return None

    for retail_store in data.get("rows", []):
        if retail_store.get("active"):
            return retail_store

    price_type = get_first("pricetype")
    if not price_type:
        print("Не найдены типы цен. Розничные документы будут пропущены.")
        return None

    print("Активная розничная точка не найдена. Пробую создать тестовую retailstore...")

    ok, data = try_api("POST", "/entity/retailstore", {
        "name": f"MSContractor Test Retail Store {run_id}",
        "organization": meta(org),
        "store": meta(store),
        "priceType": meta(price_type),
        "active": True,
        "description": "Создано скриптом для теста розничных документов mscontractor",
    })

    if ok:
        return data

    print("Не удалось создать retailstore. Розничные документы будут пропущены.")
    print(data.get("body", ""))
    return None


def ensure_retail_shift(run_id: str, retail_store: Dict[str, Any]) -> Dict[str, Any]:
    ok, data = try_api("GET", "/entity/retailshift?limit=100")
    if not ok:
        raise RuntimeError(
            "Не удалось получить список розничных смен: "
            f"{data.get('body', '')}"
        )

    retail_store_href = retail_store["meta"]["href"]
    for retail_shift in data.get("rows", []):
        shift_store = retail_shift.get("retailStore", {}).get("meta", {}).get("href")
        if shift_store == retail_store_href and not retail_shift.get("closeDate"):
            return retail_shift

    print("Открытая розничная смена не найдена. Создаю тестовую смену...")
    return create_entity("retailshift", {
        "name": f"MSContractor Test Shift {run_id}",
        "retailStore": meta(retail_store),
        "description": "Создано скриптом для розничных документов mscontractor",
    })


def create_counterparty(run_id: str) -> Dict[str, Any]:
    return create_entity("counterparty", {
        # Имя одинаковое намеренно: КА различаются по code/externalCode и ID.
        "name": COUNTERPARTY_NAME,
        "description": "Тестовый КА для проверки документов mscontractor",
        "phone": COUNTERPARTY_PHONE,
        "email": f"mscontractor-docs-{run_id}@example.com",
        "code": f"MSC-DOCS-{run_id}",
        "externalCode": f"mscontractor-docs-counterparty-{run_id}",
    })


def create_commission_contract(
    run_id: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
) -> Dict[str, Any]:
    return create_entity("contract", {
        "name": f"MSContractor Commission {run_id}",
        "moment": now_moment(),
        "ownAgent": meta(org),
        "agent": meta(cp),
        "contractType": "Commission",
        "description": "Создано скриптом для отчётов комиссионера mscontractor",
    })


def now_moment() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S.000")


def base_doc(
    run_id: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
    doc_type: str,
    applicable: bool,
) -> Dict[str, Any]:
    return {
        "moment": now_moment(),
        "applicable": applicable,
        "organization": meta(org),
        "agent": meta(cp),
        "description": f"MSContractor test document. run={run_id}, type={doc_type}",
    }


def position(product: Dict[str, Any], quantity: float = 1, price: int = 10000) -> Dict[str, Any]:
    return {
        "quantity": quantity,
        "price": price,
        "discount": 0,
        "vat": 0,
        "assortment": meta(product),
    }


def product_doc(
    run_id: str,
    doc_type: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
    product: Dict[str, Any],
    applicable: bool,
    store: Optional[Dict[str, Any]] = None,
    retail_store: Optional[Dict[str, Any]] = None,
    retail_shift: Optional[Dict[str, Any]] = None,
    quantity: float = 1,
    price: int = 10000,
) -> Dict[str, Any]:
    payload = base_doc(run_id, org, cp, doc_type, applicable)
    payload.update({
        "vatEnabled": False,
        "vatIncluded": False,
        "positions": [position(product, quantity=quantity, price=price)],
    })

    if store:
        payload["store"] = meta(store)
    if retail_store:
        payload["retailStore"] = meta(retail_store)
    if retail_shift:
        payload["retailShift"] = meta(retail_shift)

    return payload


def money_doc(
    run_id: str,
    doc_type: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
    applicable: bool,
    amount: int,
    expense_item: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    payload = base_doc(run_id, org, cp, doc_type, applicable)
    payload.update({
        "sum": amount,
        "paymentPurpose": f"MSContractor test payment. type={doc_type}, run={run_id}",
    })
    if expense_item:
        payload["expenseItem"] = meta(expense_item)
    return payload


def simple_sum_doc(
    run_id: str,
    doc_type: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
    applicable: bool,
    amount: int,
    contract: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    payload = base_doc(run_id, org, cp, doc_type, applicable)
    payload["sum"] = amount
    if contract:
        payload["contract"] = meta(contract)
    return payload


def commission_report_doc(
    run_id: str,
    doc_type: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
    applicable: bool,
    amount: int,
    contract: Optional[Dict[str, Any]],
) -> Dict[str, Any]:
    payload = simple_sum_doc(
        run_id, doc_type, org, cp, applicable, amount, contract
    )
    payload.update({
        "commissionPeriodStart": (datetime.now() - timedelta(days=1)).strftime(
            "%Y-%m-%d 00:00:00.000"
        ),
        "commissionPeriodEnd": now_moment(),
    })
    return payload


def facturein_doc(
    run_id: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
    applicable: bool,
) -> Dict[str, Any]:
    payload = simple_sum_doc(run_id, "facturein", org, cp, applicable, 21000)
    payload.update({
        "incomingNumber": f"IN-{run_id}",
        "incomingDate": now_moment(),
    })
    return payload


def factureout_doc(
    run_id: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
    applicable: bool,
) -> Dict[str, Any]:
    return simple_sum_doc(run_id, "factureout", org, cp, applicable, 22000)


def build_documents(
    selected: Set[str],
    run_id: str,
    org: Dict[str, Any],
    cp: Dict[str, Any],
    applicable: bool,
    product: Optional[Dict[str, Any]],
    store: Optional[Dict[str, Any]],
    retail_store: Optional[Dict[str, Any]],
    retail_shift: Optional[Dict[str, Any]],
    expense_item: Optional[Dict[str, Any]],
    commission_contract: Optional[Dict[str, Any]],
) -> list[Dict[str, Any]]:
    def p(
        doc_type: str,
        quantity: float,
        price: int,
        *,
        need_store: bool = False,
        need_retail_store: bool = False,
        need_retail_shift: bool = False,
    ) -> Dict[str, Any]:
        if product is None:
            raise RuntimeError(f"Для {doc_type} требуется товар")

        return product_doc(
            run_id,
            doc_type,
            org,
            cp,
            product,
            applicable,
            store=store if need_store else None,
            retail_store=retail_store if need_retail_store else None,
            retail_shift=retail_shift if need_retail_shift else None,
            quantity=quantity,
            price=price,
        )

    builders = {
        "customerorder": lambda: p("customerorder", 1, 10000),
        "demand": lambda: p("demand", 2, 11000, need_store=True),
        "invoiceout": lambda: p("invoiceout", 3, 12000),
        "invoicein": lambda: p("invoicein", 4, 13000),
        "supply": lambda: p("supply", 5, 14000, need_store=True),
        "purchaseorder": lambda: p("purchaseorder", 6, 15000),
        "cashin": lambda: money_doc(run_id, "cashin", org, cp, applicable, 16000),
        "cashout": lambda: money_doc(
            run_id, "cashout", org, cp, applicable, 17000, expense_item
        ),
        "paymentin": lambda: money_doc(run_id, "paymentin", org, cp, applicable, 18000),
        "paymentout": lambda: money_doc(
            run_id, "paymentout", org, cp, applicable, 19000, expense_item
        ),
        "salesreturn": lambda: p("salesreturn", 1, 20000, need_store=True),
        "purchasereturn": lambda: p("purchasereturn", 1, 21000, need_store=True),
        "counterpartyadjustment": lambda: simple_sum_doc(
            run_id, "counterpartyadjustment", org, cp, applicable, 22000
        ),
        "facturein": lambda: facturein_doc(run_id, org, cp, applicable),
        "factureout": lambda: factureout_doc(run_id, org, cp, applicable),
        "commissionreportin": lambda: commission_report_doc(
            run_id, "commissionreportin", org, cp, applicable, 23000, commission_contract
        ),
        "commissionreportout": lambda: commission_report_doc(
            run_id, "commissionreportout", org, cp, applicable, 24000, commission_contract
        ),
        "retaildemand": lambda: p(
            "retaildemand",
            1,
            25000,
            need_store=True,
            need_retail_store=True,
            need_retail_shift=True,
        ),
        "retailsalesreturn": lambda: p(
            "retailsalesreturn",
            1,
            26000,
            need_store=True,
            need_retail_store=True,
        ),
    }

    documents: list[Dict[str, Any]] = []
    for doc_type in sorted(selected):
        documents.append({
            "type": doc_type,
            "title": DOCUMENT_TITLES[doc_type],
            "payload": builders[doc_type](),
        })

    return documents


def entity_info(entity: Optional[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
    if entity is None:
        return None
    return {
        "id": entity.get("id"),
        "name": entity.get("name"),
        "href": entity["meta"]["href"],
    }


def write_ms_error_log(run_id: str, failed_documents: list[Dict[str, Any]]) -> Optional[Path]:
    """Write MySklad responses for failed document creation requests."""
    if not failed_documents:
        return None

    try:
        MS_ERROR_LOG_DIR.mkdir(parents=True, exist_ok=True)
        log_file = MS_ERROR_LOG_DIR / f"mscontractor_document_errors_{run_id}.json"
        log = {
            "runId": run_id,
            "createdAt": datetime.now().isoformat(timespec="seconds"),
            "baseUrl": BASE_URL,
            "failedDocuments": failed_documents,
        }
        with log_file.open("w", encoding="utf-8") as file:
            json.dump(log, file, ensure_ascii=False, indent=2)
        return log_file
    except OSError as exc:
        print(f"Не удалось записать лог ответов МС: {exc}", file=sys.stderr)
        return None


def main() -> None:
    args = parse_args()

    if args.list_documents:
        print_document_types()
        return

    try:
        counterparties_to_create = counterparty_count()
        should_write_report = write_report_json()
    except ValueError as exc:
        print(f"Ошибка: {exc}", file=sys.stderr)
        sys.exit(2)

    try:
        selected = selected_document_types(args)
    except ValueError as exc:
        print(f"Ошибка: {exc}", file=sys.stderr)
        sys.exit(2)

    validate_credentials()
    applicable = not args.draft
    run_id = datetime.now().strftime("%Y%m%d%H%M%S")

    report: Dict[str, Any] = {
        "runId": run_id,
        "baseUrl": BASE_URL,
        "applicable": applicable,
        "selectedDocumentTypes": sorted(selected),
        "counterpartyCount": counterparties_to_create,
        "writeReportJson": should_write_report,
        "counterparty": None,
        "counterparties": [],
        "supportEntities": {},
        "createdDocuments": [],
        "failedDocuments": [],
        "skippedDocuments": [],
    }

    print(f"Run ID: {run_id}")
    print(f"Количество КА: {counterparties_to_create}")
    print(f"JSON-отчёт: {'YES' if should_write_report else 'NO'}")
    print(f"Документы: {', '.join(sorted(selected))}")
    print(f"Проведение документов: {'YES' if applicable else 'NO (draft)'}")

    print("Получаю организацию...")
    org = ensure_organization()
    print(f"Организация: {org.get('name')}")

    needs_store = bool(selected & STORE_DOCUMENT_TYPES)
    needs_product = bool(selected & PRODUCT_DOCUMENT_TYPES)
    needs_retail_store = bool(selected & RETAIL_DOCUMENT_TYPES)

    store: Optional[Dict[str, Any]] = None
    product: Optional[Dict[str, Any]] = None
    retail_store: Optional[Dict[str, Any]] = None
    retail_shift: Optional[Dict[str, Any]] = None
    expense_item: Optional[Dict[str, Any]] = None

    if needs_store:
        print("Получаю/создаю склад...")
        store = ensure_store(run_id)
        print(f"Склад: {store.get('name')}")

    if needs_product:
        product = ensure_product(run_id)
        print(f"Товар: {product.get('name')}")

    if needs_retail_store:
        if store is None:
            store = ensure_store(run_id)
        retail_store = try_ensure_retail_store(run_id, org, store)
        if retail_store:
            print(f"Розничная точка: {retail_store.get('name')}")

    if needs_retail_store and retail_store:
        print("Получаю/создаю открытую розничную смену...")
        retail_shift = ensure_retail_shift(run_id, retail_store)
        print(f"Розничная смена: {retail_shift.get('name')}")

    if selected & EXPENSE_ITEM_DOCUMENT_TYPES:
        print("Получаю/создаю статью расходов...")
        expense_item = ensure_expense_item(run_id)
        print(f"Статья расходов: {expense_item.get('name')}")

    report["supportEntities"] = {
        "organization": entity_info(org),
        "store": entity_info(store),
        "product": entity_info(product),
        "retailStore": entity_info(retail_store),
        "retailShift": entity_info(retail_shift),
        "expenseItem": entity_info(expense_item),
    }

    # Если retailstore создать не удалось, не пытаемся отправлять заведомо битые retail payload'ы.
    if needs_retail_store and retail_store is None:
        for doc_type in sorted(selected & RETAIL_DOCUMENT_TYPES):
            report["skippedDocuments"].append({
                "type": doc_type,
                "title": DOCUMENT_TITLES[doc_type],
                "reason": "retailstore not found or not created",
            })
        selected -= RETAIL_DOCUMENT_TYPES

    print("\nСоздаю КА и документы...")

    total_documents = len(selected) * counterparties_to_create
    document_index = 0
    for counterparty_index in range(1, counterparties_to_create + 1):
        # Добавляем индекс, чтобы имена, email и externalCode были уникальными.
        cp_run_id = f"{run_id}-{counterparty_index:03d}"
        cp = create_counterparty(cp_run_id)
        cp_info = entity_info(cp)
        report["counterparties"].append(cp_info)
        if report["counterparty"] is None:
            report["counterparty"] = cp_info
        print(
            f"КА [{counterparty_index}/{counterparties_to_create}] создан: "
            f"{cp.get('name')} id={cp.get('id')}"
        )

        commission_contract: Optional[Dict[str, Any]] = None
        if selected & COMMISSION_REPORT_DOCUMENT_TYPES:
            print("Создаю комиссионный договор...")
            commission_contract = create_commission_contract(cp_run_id, org, cp)
            print(f"Комиссионный договор: {commission_contract.get('name')}")

        documents = build_documents(
            selected,
            cp_run_id,
            org,
            cp,
            applicable,
            product,
            store,
            retail_store,
            retail_shift,
            expense_item,
            commission_contract,
        )

        for doc in documents:
            document_index += 1
            doc_type = doc["type"]
            title = doc["title"]
            payload = doc["payload"]

            print(f"[{document_index}/{total_documents}] {doc_type} — {title}")
            ok, result = try_api("POST", f"/entity/{doc_type}", payload)

            if ok:
                print(f"  OK: id={result.get('id')} name={result.get('name')}")
                report["createdDocuments"].append({
                    "counterpartyId": cp.get("id"),
                    "counterpartyName": cp.get("name"),
                    "type": doc_type,
                    "title": title,
                    "id": result.get("id"),
                    "name": result.get("name"),
                    "applicable": result.get("applicable"),
                    "href": result["meta"]["href"],
                })
            else:
                print(f"  FAILED: status={result.get('status')}")
                report["failedDocuments"].append({
                    "counterpartyId": cp.get("id"),
                    "counterpartyName": cp.get("name"),
                    "type": doc_type,
                    "title": title,
                    "error": result,
                    "payload": payload,
                })

    ms_error_log_file = write_ms_error_log(run_id, report["failedDocuments"])

    report_file: Optional[str] = None
    if should_write_report:
        report_file = f"mscontractor_docs_report_{run_id}.json"
        with open(report_file, "w", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=2)

    print("\nГотово.")
    print(f"Создано КА: {len(report['counterparties'])}")
    print(f"Создано документов: {len(report['createdDocuments'])}")
    print(f"Ошибок: {len(report['failedDocuments'])}")
    print(f"Пропущено: {len(report['skippedDocuments'])}")
    if report_file:
        print(f"Отчёт: {report_file}")
    if ms_error_log_file:
        print(f"Лог ответов МС при ошибках: {ms_error_log_file}")

    if report["failedDocuments"]:
        print("\nЧасть документов не создалась.")
        if report_file:
            print("Подробности есть в report JSON.")
        if ms_error_log_file:
            print("Ответы МС также сохранены в отдельном JSON-логе.")
        print("Проверь поле error у неуспешного документа.")


if __name__ == "__main__":
    main()
