import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from collections import Counter
from pathlib import Path
from unittest.mock import patch
from urllib.parse import urlsplit
from uuid import UUID


SCRIPT_PATH = Path(__file__).with_name("mscontractor_delete_all_data.py")
SPEC = importlib.util.spec_from_file_location("ms_cleanup", SCRIPT_PATH)
ms_cleanup = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ms_cleanup)

TEST_PHASES = (
    ("documents", ("demand",)),
    ("counterparties", ("counterparty",)),
    ("assortment", ("product",)),
)


def uid(number):
    return str(UUID(int=number))


def api_error(entity_type, entity_id, status=409, code=3000):
    return ms_cleanup.ApiError(
        "DELETE", f"/entity/{entity_type}/{entity_id}", status,
        json.dumps({"errors": [{"code": code, "error": "test failure"}]}),
    )


class Response:
    def __init__(self, status=200, data=None, headers=None):
        self.status_code = status
        self.headers = headers or {}
        self.text = json.dumps(data) if data is not None else ""
        self.content = self.text.encode()
        self.data = data

    def json(self):
        return self.data


class Session:
    """An in-memory API whose offsets move when an entity is deleted."""

    def __init__(self, entities=None, responses=None, get_overrides=None):
        self.entities = {kind: list(ids) for kind, ids in (entities or {}).items()}
        self.responses = list(responses or [])
        self.get_overrides = get_overrides or {}
        self.headers = {}
        self.calls = []
        self.closed = False

    def request(self, method, url, params=None, **kwargs):
        path = urlsplit(url).path.split("/entity/", 1)[1]
        entity_type, _, entity_id = path.partition("/")
        self.calls.append((method, entity_type, entity_id, dict(params or {})))
        if self.responses:
            result = self.responses.pop(0)
            if isinstance(result, Exception):
                raise result
            return result
        if method == "GET":
            if entity_type in self.get_overrides:
                return self.get_overrides[entity_type]
            ids = self.entities.get(entity_type, [])
            offset, limit = params["offset"], params["limit"]
            return Response(data={
                "meta": {"size": len(ids)},
                "rows": [{"id": entity_id} for entity_id in ids[offset:offset + limit]],
            })
        if method == "DELETE":
            self.entities[entity_type].remove(entity_id)
            return Response(status=204)
        raise AssertionError(f"Unexpected request: {method} {url}")

    def close(self):
        self.closed = True


class FakeClient:
    def __init__(self, entities=None, on_delete=None, on_list=None):
        self.entities = {kind: list(ids) for kind, ids in (entities or {}).items()}
        self.on_delete, self.on_list = on_delete, on_list
        self.deletes = []
        self.scans = Counter()

    def list_ids(self, entity_type):
        self.scans[entity_type] += 1
        if self.on_list:
            self.on_list(self, entity_type)
        return list(self.entities.get(entity_type, []))

    def delete(self, entity_type, entity_id):
        self.deletes.append((entity_type, entity_id))
        if self.on_delete:
            self.on_delete(self, entity_type, entity_id)
        self.entities[entity_type].remove(entity_id)


class CleanupTestCase(unittest.TestCase):
    def setUp(self):
        self.output = io.StringIO()
        self.enterContext(contextlib.redirect_stdout(self.output))
        self.enterContext(contextlib.redirect_stderr(self.output))
        self.sleep = self.enterContext(patch.object(ms_cleanup.time, "sleep"))

    def client(self, session, execute=True, on_error=None):
        with patch.object(ms_cleanup.requests, "Session", return_value=session):
            return ms_cleanup.Client(
                "https://example.test/api/remap/1.2", token="test-token",
                execute=execute, on_error=on_error,
            )

    def cleanup(self, client, phases=TEST_PHASES, max_passes=10):
        report = {"phases": {}}
        cleanup = ms_cleanup.Cleanup(client, report, lambda: None, phases, max_passes)
        return cleanup, report


class ClientTests(CleanupTestCase):
    def test_dry_run_blocks_every_mutating_method_before_http(self):
        session = Session()
        client = self.client(session, execute=False)

        for method in ("DELETE", "POST", "PUT", "PATCH"):
            with self.subTest(method=method), self.assertRaises(RuntimeError):
                client.request(method, f"/entity/demand/{uid(1)}")
        with self.assertRaises(RuntimeError):
            client.delete("demand", uid(1))

        self.assertEqual(session.calls, [])
        self.sleep.assert_not_called()
        self.assertFalse(ms_cleanup.parse_args([]).execute)

    def test_all_pages_are_snapshotted_before_deletes_change_offsets(self):
        ids = [uid(number) for number in range(1, ms_cleanup.PAGE_SIZE + 4)]
        session = Session({"demand": ids})
        cleanup, report = self.cleanup(
            self.client(session), phases=(("documents", ("demand",)),),
        )

        self.assertTrue(cleanup.execute())

        first_delete = next(i for i, call in enumerate(session.calls) if call[0] == "DELETE")
        page_reads = session.calls[:first_delete]
        self.assertEqual([call[0] for call in page_reads], ["GET", "GET"])
        self.assertEqual([call[3]["offset"] for call in page_reads], [0, ms_cleanup.PAGE_SIZE])
        self.assertEqual(session.entities["demand"], [])
        self.assertEqual(report["phases"]["documents"]["types"]["demand"]["deletedIds"], ids)

    def test_lists_include_archived_entities_and_documents_in_trash(self):
        session = Session()
        client = self.client(session)

        for kind in ("counterparty", "product", "demand"):
            self.assertEqual(client.list_ids(kind), [])

        filters = {call[1]: set(call[3].get("filter", "").split(";")) for call in session.calls}
        for kind in ("counterparty", "product"):
            self.assertTrue({"archived=true", "archived=false"}.issubset(filters[kind]))
        self.assertTrue({"isDeleted=true", "isDeleted=false"}.issubset(filters["demand"]))

    def test_productiontask_merges_active_and_deleted_pagination(self):
        session = Session(responses=[
            Response(data={"meta": {"size": 2}, "rows": [{"id": uid(1)}]}),
            Response(data={"meta": {"size": 2}, "rows": [{"id": uid(2)}]}),
            Response(data={"meta": {"size": 1}, "rows": [{"id": uid(3)}]}),
        ])

        self.assertEqual(self.client(session).list_ids("productiontask"), [uid(1), uid(2), uid(3)])

        self.assertEqual([call[3]["filter"] for call in session.calls], ["deleted=", "deleted=", "deleted!="])
        self.assertEqual([call[3]["offset"] for call in session.calls], [0, 1, 0])

    def test_overlapping_active_and_deleted_productiontasks_stop_inventory(self):
        session = Session(responses=[
            Response(data={"meta": {"size": 1}, "rows": [{"id": uid(1)}]}),
            Response(data={"meta": {"size": 1}, "rows": [{"id": uid(1)}]}),
        ])
        cleanup, report = self.cleanup(
            self.client(session), phases=(("documents", ("productiontask",)),),
        )

        self.assertFalse(cleanup.inventory())

        entry = report["phases"]["documents"]["types"]["productiontask"]
        self.assertIsNone(entry["remainingCount"])
        self.assertTrue(entry["errors"])

    def test_processingplanfolder_does_not_use_unsupported_archived_filter(self):
        session = Session({"processingplanfolder": [uid(1)]})

        self.assertEqual(self.client(session).list_ids("processingplanfolder"), [uid(1)])

        self.assertNotIn("filter", session.calls[0][3])

    def test_malformed_or_incomplete_inventory_is_never_empty_success(self):
        invalid_payloads = (
            None,
            [],
            {"rows": "not a list", "meta": {"size": 0}},
            {"rows": [], "meta": None},
            {"rows": [], "meta": []},
            {"rows": []},
            {"rows": [], "meta": {"size": "0"}},
            {"rows": [], "meta": {"size": -1}},
            {"rows": [], "meta": {"size": True}},
            {"rows": [], "meta": {"size": 1}},
            {"rows": [{}], "meta": {"size": 1}},
            {"rows": [{"id": "not-a-uuid"}], "meta": {"size": 1}},
            {"rows": [{"id": uid(1)}], "meta": {"size": 0}},
        )
        for payload in invalid_payloads:
            with self.subTest(payload=payload):
                session = Session(responses=[Response(data=payload)])
                cleanup, report = self.cleanup(
                    self.client(session), phases=(("documents", ("demand",)),),
                )

                self.assertFalse(cleanup.inventory())

                entry = report["phases"]["documents"]["types"]["demand"]
                self.assertIsNone(entry["remainingCount"])
                self.assertTrue(entry["errors"])

    def test_duplicate_ids_across_pages_stop_inventory(self):
        session = Session(responses=[
            Response(data={"meta": {"size": 2}, "rows": [{"id": uid(1)}]}),
            Response(data={"meta": {"size": 2}, "rows": [{"id": uid(1)}]}),
        ])
        cleanup, report = self.cleanup(
            self.client(session), phases=(("documents", ("demand",)),),
        )

        self.assertFalse(cleanup.inventory())
        self.assertIsNone(report["phases"]["documents"]["types"]["demand"]["remainingCount"])

    def test_changing_size_across_pages_stops_inventory(self):
        session = Session(responses=[
            Response(data={"meta": {"size": 2}, "rows": [{"id": uid(1)}]}),
            Response(data={"meta": {"size": 3}, "rows": [{"id": uid(2)}, {"id": uid(3)}]}),
        ])
        cleanup, report = self.cleanup(
            self.client(session), phases=(("documents", ("demand",)),),
        )

        self.assertFalse(cleanup.inventory())

        self.assertIsNone(report["phases"]["documents"]["types"]["demand"]["remainingCount"])

    def test_permissions_and_unsupported_filters_do_not_count_as_empty(self):
        for status, code in ((403, 1016), (400, 1070)):
            with self.subTest(status=status, code=code):
                session = Session(responses=[Response(status, {"errors": [{"code": code}]})])
                cleanup, report = self.cleanup(
                    self.client(session), phases=(("documents", ("demand",)),),
                )

                self.assertFalse(cleanup.inventory())

                entry = report["phases"]["documents"]["types"]["demand"]
                self.assertIsNone(entry["remainingCount"])
                self.assertEqual(entry["errors"][0]["status"], status)
                self.assertEqual(len(session.calls), 1)

    def test_429_and_server_errors_retry_with_delay_before_every_attempt(self):
        errors = []
        session = Session(responses=[
            Response(429, {"errors": []}, {"Retry-After": "4", "X-Lognex-Retry-After": "5000"}),
            Response(503, {"errors": []}),
            Response(502, {"errors": []}),
            Response(data={"meta": {"size": 0}, "rows": []}),
        ])

        self.assertEqual(self.client(session, on_error=errors.append).list_ids("demand"), [])

        self.assertEqual(len(session.calls), 4)
        self.assertEqual([call.args[0] for call in self.sleep.call_args_list], [0.25, 5, 0.25, 3, 0.25, 4, 0.25])
        self.assertEqual([error.status for error in errors], [429, 503, 502])

    def test_transport_failure_is_retried_and_recorded(self):
        errors = []
        session = Session(responses=[
            ms_cleanup.requests.ConnectionError("connection reset"),
            Response(data={"meta": {"size": 0}, "rows": []}),
        ])

        self.assertEqual(self.client(session, on_error=errors.append).list_ids("demand"), [])

        self.assertEqual([error.status for error in errors], [0])
        self.assertEqual([call.args[0] for call in self.sleep.call_args_list], [0.25, 1, 0.25])

    def test_transient_retries_are_bounded(self):
        session = Session(responses=[Response(503, {"errors": []}) for _ in range(4)])

        with self.assertRaises(ms_cleanup.ApiError) as raised:
            self.client(session).list_ids("demand")

        self.assertEqual(raised.exception.status, 503)
        self.assertEqual(len(session.calls), 4)


class PhaseOrderTests(CleanupTestCase):
    def test_documents_then_counterparties_then_assortment(self):
        client = FakeClient({"demand": [uid(1)], "counterparty": [uid(2)], "product": [uid(3)]})
        cleanup, report = self.cleanup(client)

        self.assertTrue(cleanup.execute())

        self.assertEqual(client.deletes, [("demand", uid(1)), ("counterparty", uid(2)), ("product", uid(3))])
        self.assertTrue(all(phase["status"] == "completed" for phase in report["phases"].values()))

    def test_document_dependency_blocks_counterparties_and_assortment(self):
        def deny_document(client, kind, entity_id):
            if kind == "demand":
                raise api_error(kind, entity_id)

        client = FakeClient(
            {"demand": [uid(1)], "counterparty": [uid(2)], "product": [uid(3)]},
            on_delete=deny_document,
        )
        cleanup, report = self.cleanup(client)
        self.assertTrue(cleanup.inventory())

        self.assertFalse(cleanup.execute())

        self.assertEqual(client.deletes, [("demand", uid(1))])
        self.assertEqual(report["phases"]["documents"]["status"], "blocked")
        self.assertEqual(report["phases"]["counterparties"]["status"], "pending")
        self.assertEqual(report["phases"]["assortment"]["status"], "pending")
        self.assertEqual(report["phases"]["documents"]["types"]["demand"]["remainingCount"], 1)

    def test_counterparty_dependency_blocks_assortment(self):
        def deny_counterparty(client, kind, entity_id):
            if kind == "counterparty":
                raise api_error(kind, entity_id)

        client = FakeClient(
            {"demand": [uid(1)], "counterparty": [uid(2)], "product": [uid(3)]},
            on_delete=deny_counterparty,
        )
        cleanup, report = self.cleanup(client)

        self.assertFalse(cleanup.execute())

        self.assertEqual(client.deletes, [("demand", uid(1)), ("counterparty", uid(2))])
        self.assertEqual(report["phases"]["documents"]["status"], "completed")
        self.assertEqual(report["phases"]["counterparties"]["status"], "blocked")
        self.assertEqual(report["phases"]["assortment"]["status"], "pending")

    def test_fatal_delete_error_stops_before_next_id_or_phase(self):
        for status in (0, 401, 429, 503):
            with self.subTest(status=status):
                def unavailable(client, kind, entity_id):
                    raise api_error(kind, entity_id, status)

                client = FakeClient(
                    {"demand": [uid(1), uid(2)], "counterparty": [uid(3)]},
                    on_delete=unavailable,
                )
                cleanup, report = self.cleanup(client)

                self.assertFalse(cleanup.execute())

                self.assertEqual(client.deletes, [("demand", uid(1))])
                self.assertEqual(report["phases"]["documents"]["status"], "blocked")
                self.assertEqual(report["phases"]["counterparties"]["status"], "pending")
                self.assertEqual(report["phases"]["documents"]["types"]["demand"]["errors"][0]["status"], status)

    def test_fatal_inventory_error_stops_before_further_requests(self):
        for status in (0, 401, 429, 503):
            with self.subTest(status=status):
                def unavailable(client, kind):
                    raise ms_cleanup.ApiError("GET", f"/entity/{kind}", status, "unavailable")

                client = FakeClient(on_list=unavailable)
                cleanup, report = self.cleanup(client)

                with self.assertRaises(ms_cleanup.ApiError):
                    cleanup.inventory()

                self.assertEqual(client.scans, {"demand": 1})
                self.assertEqual(report["phases"]["documents"]["status"], "blocked")
                self.assertEqual(report["phases"]["counterparties"]["status"], "pending")

    def test_failed_rescan_clears_previously_saved_remaining_ids(self):
        def malformed_second_scan(client, kind):
            if client.scans[kind] == 2:
                raise RuntimeError("Malformed API response")

        client = FakeClient({"demand": [uid(1)]}, on_list=malformed_second_scan)
        cleanup, report = self.cleanup(client, phases=(("documents", ("demand",)),))
        self.assertTrue(cleanup.inventory())

        self.assertFalse(cleanup.scan("documents"))

        entry = report["phases"]["documents"]["types"]["demand"]
        self.assertIsNone(entry["remainingIds"])
        self.assertIsNone(entry["remainingCount"])
        self.assertNotIn("demand", cleanup.pending)

    def test_another_pass_resolves_dependencies_inside_one_phase(self):
        def linked_return(client, kind, entity_id):
            if kind == "demand" and client.entities["salesreturn"]:
                raise api_error(kind, entity_id)

        client = FakeClient({"demand": [uid(1)], "salesreturn": [uid(2)]}, on_delete=linked_return)
        cleanup, report = self.cleanup(client, phases=(("documents", ("demand", "salesreturn")),))

        self.assertTrue(cleanup.execute())

        self.assertEqual(client.deletes, [("demand", uid(1)), ("salesreturn", uid(2)), ("demand", uid(1))])
        self.assertEqual(report["phases"]["documents"]["types"]["demand"]["deletedIds"], [uid(1)])
        self.assertEqual(report["phases"]["documents"]["types"]["demand"]["errors"], [])

    def test_read_only_document_is_a_blocker_before_any_delete(self):
        kind = sorted(ms_cleanup.READ_ONLY_TYPES)[0]
        client = FakeClient({kind: [uid(1)], "demand": [uid(2)], "counterparty": [uid(3)]})
        cleanup, report = self.cleanup(client, phases=(
            ("documents", (kind, "demand")), ("counterparties", ("counterparty",)),
        ))

        self.assertFalse(cleanup.inventory())
        self.assertFalse(cleanup.execute())

        self.assertEqual(client.deletes, [])
        self.assertEqual(report["phases"]["documents"]["status"], "blocked")
        self.assertTrue(report["phases"]["documents"]["types"][kind]["errors"])

    def test_confirmed_already_absent_404_is_idempotent(self):
        def removed_concurrently(client, kind, entity_id):
            client.entities[kind].remove(entity_id)
            raise api_error(kind, entity_id, 404, 1021)

        client = FakeClient({"demand": [uid(1)]}, on_delete=removed_concurrently)
        cleanup, report = self.cleanup(client)

        self.assertTrue(cleanup.execute())

        entry = report["phases"]["documents"]["types"]["demand"]
        self.assertEqual(entry["alreadyAbsentIds"], [uid(1)])
        self.assertEqual(entry["deletedIds"], [])
        self.assertEqual(entry["errors"], [])

    def test_arbitrary_404_does_not_count_as_success(self):
        def wrong_route(client, kind, entity_id):
            raise api_error(kind, entity_id, 404, 1005)

        client = FakeClient({"demand": [uid(1)], "counterparty": [uid(2)]}, on_delete=wrong_route)
        cleanup, report = self.cleanup(client)

        self.assertFalse(cleanup.execute())

        entry = report["phases"]["documents"]["types"]["demand"]
        self.assertEqual(entry["alreadyAbsentIds"], [])
        self.assertEqual(entry["deletedIds"], [])
        self.assertEqual(entry["errors"][0]["status"], 404)
        self.assertEqual(client.deletes, [("demand", uid(1))])

    def test_reappearing_document_stops_transition_to_counterparties(self):
        def recreate_document(client, kind):
            if kind == "demand" and client.scans[kind] == 3:
                client.entities[kind].append(uid(4))

        client = FakeClient(
            {"demand": [uid(1)], "counterparty": [uid(2)], "product": [uid(3)]},
            on_list=recreate_document,
        )
        cleanup, report = self.cleanup(client)

        self.assertFalse(cleanup.execute())

        self.assertEqual(client.deletes, [("demand", uid(1))])
        self.assertEqual(report["phases"]["counterparties"]["status"], "blocked")
        self.assertEqual(report["phases"]["documents"]["types"]["demand"]["remainingIds"], [uid(4)])

    def test_final_verification_detects_document_created_during_assortment_delete(self):
        def recreate_document(client, kind, entity_id):
            if kind == "product":
                client.entities["demand"] = [uid(4)]

        client = FakeClient({"product": [uid(3)]}, on_delete=recreate_document)
        cleanup, report = self.cleanup(client)

        self.assertFalse(cleanup.execute())

        self.assertEqual(report["phases"]["documents"]["status"], "blocked")
        self.assertEqual(report["phases"]["documents"]["types"]["demand"]["remainingCount"], 1)

    def test_successful_delete_response_still_requires_empty_rescan(self):
        client = FakeClient({"demand": [uid(1)], "counterparty": [uid(2)]})
        cleanup, report = self.cleanup(client, max_passes=2)
        with patch.object(client, "delete") as delete:
            self.assertFalse(cleanup.execute())

        self.assertEqual(delete.call_count, 2)
        self.assertEqual(report["phases"]["documents"]["status"], "blocked")
        self.assertEqual(report["phases"]["counterparties"]["status"], "pending")


class CommandTests(CleanupTestCase):
    def run_command(self, session, *args):
        with tempfile.TemporaryDirectory() as directory:
            report_path = Path(directory) / "report.json"
            with patch.dict(ms_cleanup.os.environ, {"MS_TOKEN": "test-token"}, clear=True), \
                    patch.object(ms_cleanup.requests, "Session", return_value=session):
                code = ms_cleanup.main([
                    "--env-file", str(Path(directory) / "missing.env"),
                    "--report", str(report_path), *args,
                ])
            report = json.loads(report_path.read_text(encoding="utf-8"))
            self.assertFalse(report_path.with_name("report.json.tmp").exists())
        self.assertTrue(session.closed)
        return code, report

    def test_default_dry_run_writes_inventory_and_never_deletes(self):
        session = Session({"demand": [uid(1)], "counterparty": [uid(2)], "product": [uid(3)]})

        code, report = self.run_command(session)

        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "planned")
        self.assertEqual(report["mode"], "dry-run")
        self.assertTrue(all(call[0] == "GET" for call in session.calls))
        for phase, kinds in TEST_PHASES:
            self.assertEqual(report["phases"][phase]["types"][kinds[0]]["initialCount"], 1)
        self.assertIn("finishedAt", report)

    def test_execute_writes_completed_report_after_all_phases(self):
        session = Session({"demand": [uid(1)], "counterparty": [uid(2)], "product": [uid(3)]})

        code, report = self.run_command(session, "--execute")

        self.assertEqual(code, 0)
        self.assertEqual(report["status"], "completed")
        self.assertEqual(report["mode"], "execute")
        self.assertEqual([call[1] for call in session.calls if call[0] == "DELETE"], ["demand", "counterparty", "product"])
        self.assertTrue(all(phase["status"] == "completed" for phase in report["phases"].values()))

    def test_forbidden_document_inventory_exits_nonzero_and_saves_error(self):
        for mode in ("--dry-run", "--execute"):
            with self.subTest(mode=mode):
                session = Session(
                    {"counterparty": [uid(2)], "product": [uid(3)]},
                    get_overrides={"demand": Response(403, {"errors": [{"code": 1016}]})},
                )

                code, report = self.run_command(session, mode)

                self.assertEqual(code, 1)
                self.assertEqual(report["status"], "incomplete")
                self.assertTrue(all(call[0] == "GET" for call in session.calls))
                self.assertEqual(report["apiErrors"][0]["status"], 403)
                entry = report["phases"]["documents"]["types"]["demand"]
                self.assertIsNone(entry["remainingCount"])
                self.assertTrue(entry["errors"])


if __name__ == "__main__":
    unittest.main()
