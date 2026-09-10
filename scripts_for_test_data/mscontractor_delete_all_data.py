#!/usr/bin/env python3
"""Очистка данных МойСклад: документы -> контрагенты -> ассортимент.

По умолчанию выполняются только GET-запросы. Удаление включает --execute.
Настройки подключения читаются из соседнего .env.test_data и окружения.
"""

import argparse
import json
import math
import os
import shlex
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit
from uuid import UUID

import requests


# Полный список документов JSON API, независимый от MS_DOCUMENT_TYPES генератора.
# Сначала зависимые документы, затем основания. Повторные проходы разрешают
# дополнительные связи между документами одного этапа.
DOCUMENT_TYPES = (
    "bonustransaction", "task", "factureout", "facturein", "retailsalesreturn", "prepaymentreturn",
    "loss",
    "salesreturn", "purchasereturn", "commissionreportin", "commissionreportout",
    "retaildrawercashin", "retaildrawercashout", "retaildemand", "prepayment",
    "retailshift", "paymentin", "paymentout", "cashin", "cashout",
    "productionstagecompletion", "processing", "productiontask", "processingorder",
    "demand", "supply", "invoiceout", "invoicein", "customerorder", "purchaseorder",
    "move", "enter", "inventory", "internalorder", "counterpartyadjustment",
    "payroll", "pricelist", "contract", "emissionorder", "retireorder",
)
ASSORTMENT_TYPES = (
    "processingplan", "processingplanfolder", "processingprocess", "processingstage",
    "consignment", "bundle", "variant", "product", "service", "productfolder",
)
PHASES = (
    ("documents", DOCUMENT_TYPES),
    ("counterparties", ("counterparty",)),
    ("assortment", ASSORTMENT_TYPES),
)
PHASE_TITLES = {
    "documents": "Документы и договоры",
    "counterparties": "Контрагенты",
    "assortment": "Ассортимент и производственные справочники",
}
# processingplanfolder возвращает список без фильтрации (filter=archived API
# отклоняет с 1034, хотя поле archived присутствует в самой сущности).
ARCHIVED_TYPES = frozenset(ASSORTMENT_TYPES + ("contract", "counterparty")) - {"processingplanfolder"}
# В API этих документов нет операции удаления самого документа.
READ_ONLY_TYPES = frozenset(("emissionorder", "retireorder"))
NO_TRASH_TYPES = frozenset(("emissionorder", "payroll", "productionstagecompletion", "bonustransaction", "task"))
PAGE_SIZE = 1000
DEFAULT_BASE_URL = "https://api.moysklad.ru/api/remap/1.2"


def load_env(path):
    if not path.is_file():
        return
    for number, raw_line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[7:].lstrip()
        if "=" not in line or not line.split("=", 1)[0].strip():
            raise ValueError(f"Некорректная строка {number} в {path.name}")
        key, raw_value = line.split("=", 1)
        try:
            value = " ".join(shlex.split(raw_value, comments=True))
        except ValueError as exc:
            raise ValueError(f"Некорректное значение в строке {number} в {path.name}") from exc
        os.environ.setdefault(key.strip(), value)


class ApiError(Exception):
    def __init__(self, method, path, status, body):
        super().__init__(f"{method} {path}: HTTP {status}")
        self.method, self.path, self.status, self.body = method, path, status, body

    @property
    def codes(self):
        try:
            data = json.loads(self.body)
            return {str(item.get("code")) for item in data.get("errors", [])}
        except (ValueError, AttributeError, TypeError):
            return set()

    def as_dict(self):
        return {"method": self.method, "path": self.path,
                "status": self.status, "body": self.body}


class Client:
    def __init__(self, base_url, token="", login="", password="", delay=0.25,
                 execute=False, on_error=None):
        parsed = urlsplit(base_url)
        if (parsed.scheme not in ("http", "https") or not parsed.netloc
                or parsed.username or parsed.password or parsed.query or parsed.fragment):
            raise ValueError("MS_BASE_URL должен быть HTTP(S) URL без credentials/query/fragment")
        if not math.isfinite(delay) or delay < 0:
            raise ValueError("MS_REQUEST_DELAY_SECONDS должен быть конечным числом >= 0")
        if not token and not (login and password):
            raise ValueError("Задай MS_TOKEN или MS_LOGIN и MS_PASSWORD в .env.test_data")
        self.base_url = base_url.rstrip("/")
        self.delay, self.execute, self.on_error = delay, execute, on_error
        self.session = requests.Session()
        self.session.headers.update({
            "Accept": "application/json;charset=utf-8", "Accept-Encoding": "gzip",
            "User-Agent": "mscontractor-delete-all-data/1.0",
        })
        if token:
            self.session.headers["Authorization"] = f"Bearer {token}"
        else:
            self.session.auth = (login, password)

    def request(self, method, path, params=None):
        if method != "GET" and not self.execute:
            raise RuntimeError("Изменение данных запрещено без --execute")
        for attempt in range(4):
            time.sleep(self.delay)
            try:
                response = self.session.request(
                    method, self.base_url + path, params=params,
                    timeout=60, allow_redirects=False,
                )
            except requests.RequestException as exc:
                error = ApiError(method, path, 0, str(exc))
                if self.on_error:
                    self.on_error(error)
                if attempt == 3:
                    raise error from exc
                time.sleep(2 ** attempt)
                continue
            if 200 <= response.status_code < 300:
                if method == "DELETE" or not response.content:
                    return None
                try:
                    return response.json()
                except ValueError as exc:
                    raise ApiError(method, path, response.status_code, response.text) from exc
            error = ApiError(method, path, response.status_code, response.text)
            if self.on_error:
                self.on_error(error)
            if attempt == 3 or (response.status_code != 429 and response.status_code < 500):
                raise error
            # Retry-After задаётся в секундах, X-Lognex-Retry-After — в миллисекундах.
            wait = max(3, 2 ** attempt)
            for header, divisor in (("Retry-After", 1), ("X-Lognex-Retry-After", 1000)):
                try:
                    value = float(response.headers.get(header, "0")) / divisor
                    if math.isfinite(value):
                        wait = max(wait, value)
                except ValueError:
                    pass
            time.sleep(wait)

    def list_ids(self, entity_type):
        if entity_type == "productiontask":
            # Этот endpoint не принимает isDeleted. Читаем непустой и пустой
            # deleted отдельно, чтобы включить производственные задания из корзины.
            active = self._list_ids(entity_type, "deleted=")
            deleted = self._list_ids(entity_type, "deleted!=")
            if set(active).intersection(deleted):
                raise RuntimeError("productiontask: пересечение активных и удалённых заданий")
            return active + deleted
        if entity_type in ARCHIVED_TYPES:
            filter_value = "archived=true;archived=false"
        elif entity_type in DOCUMENT_TYPES and entity_type not in NO_TRASH_TYPES:
            filter_value = "isDeleted=true;isDeleted=false"
        else:
            filter_value = None
        return self._list_ids(entity_type, filter_value)

    def _list_ids(self, entity_type, filter_value):
        ids, seen, offset = [], set(), 0
        expected_size = None
        while True:
            params = {"limit": PAGE_SIZE, "offset": offset}
            if filter_value is not None:
                params["filter"] = filter_value
            data = self.request("GET", f"/entity/{entity_type}", params=params)
            if not isinstance(data, dict) or not isinstance(data.get("rows"), list):
                raise RuntimeError(f"{entity_type}: API не вернул список rows")
            metadata = data.get("meta")
            size = metadata.get("size") if isinstance(metadata, dict) else None
            if type(size) is not int or size < 0:
                raise RuntimeError(f"{entity_type}: API не вернул корректный meta.size")
            if expected_size is None:
                expected_size = size
            elif size != expected_size:
                raise RuntimeError(f"{entity_type}: размер списка изменился; останови запись в МС")
            rows = data["rows"]
            for row in rows:
                entity_id = str(UUID(row["id"]))
                if entity_id in seen:
                    raise RuntimeError(f"{entity_type}: повтор ID при пагинации; останови запись в МС")
                seen.add(entity_id)
                ids.append(entity_id)
            offset += len(rows)
            if offset > size:
                raise RuntimeError(f"{entity_type}: количество rows превышает meta.size")
            if offset == size:
                return ids
            if not rows:
                raise RuntimeError(f"{entity_type}: неполная страница API при offset={offset}")

    def delete(self, entity_type, entity_id):
        self.request("DELETE", f"/entity/{entity_type}/{entity_id}")


class Cleanup:
    def __init__(self, client, report, save, phases=PHASES, max_passes=10):
        self.client, self.report, self.save = client, report, save
        self.phases, self.max_passes = phases, max_passes
        self.pending = {}
        for phase, types in phases:
            report["phases"][phase] = {"status": "pending", "types": {}}
            for entity_type in types:
                report["phases"][phase]["types"][entity_type] = {
                    "initialCount": None, "remainingCount": None,
                    "deletedIds": [], "alreadyAbsentIds": [], "errors": [],
                }

    def scan(self, phase, initial=False):
        complete = True
        for entity_type, entry in self.report["phases"][phase]["types"].items():
            entry["errors"] = []
            try:
                ids = self.client.list_ids(entity_type)
                self.pending[entity_type] = ids
                entry["remainingCount"] = len(ids)
                entry["remainingIds"] = ids
                if initial:
                    entry["initialCount"] = len(ids)
                if entity_type in READ_ONLY_TYPES and ids:
                    entry["errors"].append({"message": "JSON API не поддерживает удаление этого типа"})
                    complete = False
                if initial or ids:
                    print(f"  {entity_type}: {len(ids)}")
            except (ApiError, RuntimeError, ValueError, KeyError, TypeError) as exc:
                self.pending.pop(entity_type, None)
                entry["remainingCount"] = None
                entry["remainingIds"] = None
                entry["errors"].append(exc.as_dict() if isinstance(exc, ApiError)
                                       else {"message": str(exc)})
                complete = False
                print(f"  {entity_type}: не удалось проверить ({exc})", file=sys.stderr)
                if isinstance(exc, ApiError) and (exc.status in (0, 401, 429) or exc.status >= 500):
                    self.report["phases"][phase]["status"] = "blocked"
                    self.save()
                    raise
            self.save()
        return complete

    def inventory(self):
        complete = True
        for phase, _ in self.phases:
            print(f"{PHASE_TITLES.get(phase, phase)}:")
            if not self.scan(phase, initial=True):
                complete = False
        return complete

    def execute(self):
        for phase, types in self.phases:
            phase_report = self.report["phases"][phase]
            phase_report["status"] = "running"
            # Новый снимок этапа и проверка предыдущих этапов перед каждым переходом.
            for earlier_phase, _ in self.phases:
                if earlier_phase == phase:
                    break
                if not self.scan(earlier_phase) or any(
                    item["remainingCount"] for item in self.report["phases"][earlier_phase]["types"].values()
                ):
                    self.report["phases"][earlier_phase]["status"] = "blocked"
                    phase_report["status"] = "blocked"
                    self.report["message"] = f"Вновь обнаружены данные/ошибки этапа {earlier_phase}"
                    self.save()
                    return False
            if not self.scan(phase):
                phase_report["status"] = "blocked"
                self.report["message"] = f"Не удалось полностью проверить этап {phase}"
                self.save()
                return False
            for pass_number in range(1, self.max_passes + 1):
                print(f"{PHASE_TITLES.get(phase, phase)}: проход {pass_number}")
                progress = 0
                checkpoint_at = time.monotonic()
                since_checkpoint = 0
                delete_errors = {}
                for entity_type in types:
                    entry = phase_report["types"][entity_type]
                    # Все страницы уже прочитаны: DELETE не сдвигает offset выборки.
                    for entity_id in self.pending[entity_type]:
                        try:
                            self.client.delete(entity_type, entity_id)
                            entry["deletedIds"].append(entity_id)
                            progress += 1
                        except ApiError as exc:
                            if exc.status == 404 and exc.codes == {"1021"}:
                                entry["alreadyAbsentIds"].append(entity_id)
                                progress += 1
                            else:
                                delete_errors.setdefault(entity_type, []).append(
                                    {"id": entity_id, **exc.as_dict()})
                                print(f"  Не удалён {entity_type}/{entity_id}: {exc}", file=sys.stderr)
                                if exc.status in (0, 401, 429) or exc.status >= 500:
                                    entry["errors"].extend(delete_errors[entity_type])
                                    phase_report["status"] = "blocked"
                                    self.report["message"] = f"API недоступен: {exc}"
                                    self.save()
                                    return False
                        since_checkpoint += 1
                        if since_checkpoint >= 50 or time.monotonic() - checkpoint_at >= 5:
                            self.save()
                            since_checkpoint = 0
                            checkpoint_at = time.monotonic()
                # DELETE 2xx сам по себе не доказывает пустоту всего этапа.
                scanned = self.scan(phase)
                for entity_type, errors in delete_errors.items():
                    remaining = set(self.pending.get(entity_type, []))
                    phase_report["types"][entity_type]["errors"].extend(
                        error for error in errors if not scanned or error["id"] in remaining)
                if scanned and all(entry["remainingCount"] == 0 for entry in phase_report["types"].values()):
                    phase_report["status"] = "completed"
                    self.save()
                    break
                if not scanned or not progress or pass_number == self.max_passes:
                    phase_report["status"] = "blocked"
                    self.report["message"] = f"Этап {phase} не очищен; следующие этапы не запущены"
                    self.save()
                    return False
            self.save()
        # Внешняя интеграция могла создать документы во время очистки ассортимента.
        verified = True
        for phase, _ in self.phases:
            if not self.scan(phase) or any(
                item["remainingCount"] for item in self.report["phases"][phase]["types"].values()
            ):
                self.report["phases"][phase]["status"] = "blocked"
                verified = False
        return verified


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--execute", action="store_true", help="Безвозвратно удалить все данные перечисленных типов")
    mode.add_argument("--dry-run", action="store_true", help="Только посчитать данные (по умолчанию)")
    parser.add_argument("--env-file", type=Path, default=Path(__file__).with_name(".env.test_data"))
    parser.add_argument("--report", type=Path, help="Путь к JSON-отчёту")
    parser.add_argument("--max-passes", type=int, default=10, help="Максимум проходов одного этапа (по умолчанию 10)")
    args = parser.parse_args(argv)
    if args.max_passes < 1:
        parser.error("--max-passes должен быть >= 1")
    return args


def main(argv=None):
    args = parse_args(argv)
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S_%fZ")
    report_path = args.report or Path(__file__).with_name("Logs_MS_test_data") / f"mscontractor_cleanup_report_{run_id}.json"
    report = {"runId": run_id, "mode": "execute" if args.execute else "dry-run",
              "status": "running", "phases": {}, "apiErrors": []}

    def save():
        report_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = report_path.with_name(report_path.name + ".tmp")
        temporary.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        temporary.replace(report_path)

    def record_error(error):
        report["apiErrors"].append(error.as_dict())
        save()

    client = None
    code = 1
    try:
        # Проверяем возможность сохранить отчёт до первого сетевого запроса.
        save()
        load_env(args.env_file)
        client = Client(
            os.getenv("MS_BASE_URL", DEFAULT_BASE_URL), token=os.getenv("MS_TOKEN", ""),
            login=os.getenv("MS_LOGIN", ""), password=os.getenv("MS_PASSWORD", ""),
            delay=float(os.getenv("MS_REQUEST_DELAY_SECONDS", "0.25")),
            execute=args.execute, on_error=record_error,
        )
        report["baseUrl"] = client.base_url
        print(f"МойСклад: {client.base_url}")
        print("Режим: УДАЛЕНИЕ ВСЕХ ДАННЫХ УКАЗАННЫХ ТИПОВ" if args.execute else "Режим: просмотр, без удаления")
        cleanup = Cleanup(client, report, save, max_passes=args.max_passes)
        inventory_ok = cleanup.inventory()
        if args.execute:
            success = cleanup.execute()
            report["status"] = "completed" if success else "incomplete"
            code = 0 if success else 1
        else:
            report["status"] = "planned" if inventory_ok else "incomplete"
            code = 0 if inventory_ok else 1
    except KeyboardInterrupt:
        report["status"] = "interrupted"
        report["message"] = "Остановлено пользователем; повторный запуск перечитает данные из МС"
        code = 130
    except Exception as exc:
        report["status"] = "failed"
        report["message"] = str(exc)
    finally:
        if client is not None:
            client.session.close()
        report["finishedAt"] = datetime.now(timezone.utc).isoformat()
        try:
            save()
        except OSError as exc:
            print(f"Не удалось сохранить отчёт: {exc}", file=sys.stderr)
            code = 1
        print(f"Результат: {report['status']}. {report.get('message', '')}")
        print(f"Отчёт: {report_path}")
    return code


if __name__ == "__main__":
    sys.exit(main())
