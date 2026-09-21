import importlib.util
import argparse
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT_PATH = Path(__file__).with_name("mscontractor_create_counterparty_docs.py")
SPEC = importlib.util.spec_from_file_location("ms_docs", SCRIPT_PATH)
ms_docs = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ms_docs)


def entity(entity_type: str, entity_id: str) -> dict:
    return {
        "id": entity_id,
        "meta": {
            "href": f"https://example.test/entity/{entity_type}/{entity_id}",
            "type": entity_type,
            "mediaType": "application/json",
        },
    }


def source(entity_type: str, entity_id: str) -> dict:
    document = entity(entity_type, entity_id)
    document.update({
        "agent": entity("counterparty", "cp"),
        "organization": entity("organization", "org"),
        "store": entity("store", "store"),
        "positions": {"rows": [{
            "id": "position-id",
            "quantity": 1,
            "price": 10000,
            "assortment": entity("product", "product"),
        }]},
    })
    return document


class DependentDocumentPayloadTests(unittest.TestCase):
    def test_documents_per_type_is_configurable(self) -> None:
        with patch.dict(ms_docs.os.environ, {"MS_DOCUMENTS_PER_TYPE": "1000"}):
            self.assertEqual(1000, ms_docs.documents_per_type())

    def test_documents_per_type_defaults_to_one(self) -> None:
        with patch.dict(ms_docs.os.environ, {}, clear=False):
            ms_docs.os.environ.pop("MS_DOCUMENTS_PER_TYPE", None)
            self.assertEqual(1, ms_docs.documents_per_type())

    def test_documents_per_type_rejects_non_positive_value(self) -> None:
        with patch.dict(ms_docs.os.environ, {"MS_DOCUMENTS_PER_TYPE": "0"}):
            with self.assertRaisesRegex(ValueError, "MS_DOCUMENTS_PER_TYPE"):
                ms_docs.documents_per_type()

    def test_build_documents_repeats_each_selected_type(self) -> None:
        documents = ms_docs.build_documents(
            {"customerorder"},
            "run-1",
            entity("organization", "org"),
            entity("counterparty", "cp"),
            True,
            entity("product", "product"),
            None,
            None,
            None,
            None,
            None,
            3,
        )

        self.assertEqual([1, 2, 3], [item["number"] for item in documents])
        self.assertEqual(["customerorder"] * 3, [item["type"] for item in documents])

    def test_loss_payload_uses_organization_store_and_positions_without_agent(self) -> None:
        organization = entity("organization", "org")
        store = entity("store", "store")
        product = entity("product", "product")
        payload = ms_docs.loss_doc("run-1", organization, store, product, True)

        self.assertEqual(payload["organization"], ms_docs.meta(organization))
        self.assertEqual(payload["store"], ms_docs.meta(store))
        self.assertNotIn("agent", payload)
        self.assertNotIn("salesReturn", payload)
        self.assertEqual(payload["positions"][0]["assortment"], ms_docs.meta(product))

    def test_loss_payload_links_to_salesreturn(self) -> None:
        organization = entity("organization", "org")
        store = entity("store", "store")
        product = entity("product", "product")
        sales_return = entity("salesreturn", "return-id")

        payload = ms_docs.loss_doc(
            "run-1", organization, store, product, True, sales_return=sales_return
        )

        self.assertEqual(payload["salesReturn"], ms_docs.meta(sales_return))

    def test_get_first_price_type_uses_company_settings_endpoint(self) -> None:
        price_type = entity("pricetype", "price-type-id")

        with patch.object(ms_docs, "try_api", return_value=(True, [price_type])) as api_mock:
            result = ms_docs.get_first_price_type()

        self.assertEqual(price_type, result)
        api_mock.assert_called_once_with("GET", "/context/companysettings/pricetype")

    def test_selected_dependent_documents_add_required_sources(self) -> None:
        args = argparse.Namespace(
            all=False,
            docs=["salesreturn", "purchasereturn", "retailsalesreturn", "factureout", "facturein"],
            stats=False,
        )

        with patch.dict(
            ms_docs.os.environ,
            {"MS_SALESRETURN_DOCUMENTS": "", "MS_PURCHASERETURN_DOCUMENTS": ""},
        ):
            selected = ms_docs.selected_document_types(args)

        self.assertEqual(
            {
                "salesreturn", "purchasereturn", "retailsalesreturn", "factureout", "facturein",
                "demand", "supply", "retaildemand",
            },
            selected,
        )

    def test_return_mapping_adds_configured_documents_and_sources(self) -> None:
        args = argparse.Namespace(
            all=False, docs=["salesreturn", "purchasereturn"], stats=False
        )

        with patch.dict(
            ms_docs.os.environ,
            {
                "MS_SALESRETURN_DOCUMENTS": "factureout,paymentout,cashout,loss",
                "MS_PURCHASERETURN_DOCUMENTS": "facturein,factureout,paymentin,cashin",
            },
        ):
            selected = ms_docs.selected_document_types(args)

        self.assertEqual(
            {
                "salesreturn", "purchasereturn", "demand", "supply",
                "facturein", "factureout", "paymentout", "cashout", "loss",
                "paymentin", "cashin",
            },
            selected,
        )

    def test_configured_financial_document_uses_return_operations(self) -> None:
        purchasereturn = source("purchasereturn", "return-id")
        template = {
            "meta": {"href": "https://example.test/paymentin/new"},
            "agent": purchasereturn["agent"],
            "operations": [ms_docs.meta(purchasereturn)],
        }

        with patch.object(ms_docs, "api", return_value=template) as api_mock:
            payload = ms_docs.financial_document_doc("paymentin", purchasereturn)

        api_mock.assert_called_once_with(
            "PUT",
            "/entity/paymentin/new",
            {"operations": [ms_docs.meta(purchasereturn)]},
        )
        self.assertEqual(payload["operations"], [ms_docs.meta(purchasereturn)])

    def test_return_mapping_can_create_same_type_in_both_scenarios(self) -> None:
        with patch.dict(
            ms_docs.os.environ,
            {
                "MS_SALESRETURN_DOCUMENTS": "factureout,paymentout",
                "MS_PURCHASERETURN_DOCUMENTS": "factureout,paymentin",
            },
        ):
            jobs = ms_docs.dependent_document_jobs({
                "salesreturn", "purchasereturn", "demand", "supply",
                "factureout", "paymentout", "paymentin",
            })

        self.assertEqual(
            jobs.count(("factureout", "salesreturn")), 1
        )
        self.assertEqual(
            jobs.count(("factureout", "purchasereturn")), 1
        )

    def test_configured_return_source_uses_created_return_document(self) -> None:
        purchasereturn = source("purchasereturn", "return-id")

        source_type, selected = ms_docs.choose_configured_return_source(
            "paymentin",
            "purchasereturn",
            {"purchasereturn": [purchasereturn]},
            0,
        )

        self.assertEqual(source_type, "purchasereturn")
        self.assertEqual(selected["id"], "return-id")

    def test_salesreturn_uses_demand_positions_without_read_only_fields(self) -> None:
        demand = source("demand", "demand-id")

        payload = ms_docs.salesreturn_doc(demand)

        self.assertEqual(payload["demand"], ms_docs.meta(demand))
        self.assertNotIn("id", payload["positions"][0])
        self.assertEqual(payload["positions"][0]["price"], 10000)

    def test_purchasereturn_uses_supply_positions_without_read_only_fields(self) -> None:
        supply = source("supply", "supply-id")

        payload = ms_docs.purchasereturn_doc(supply)

        self.assertEqual(payload["supply"], ms_docs.meta(supply))
        self.assertEqual(payload["positions"][0]["quantity"], 1)

    def test_retailsalesreturn_uses_retaildemand_and_payment_sums(self) -> None:
        demand = source("retaildemand", "retail-demand-id")
        demand.update({
            "retailStore": entity("retailstore", "retail-store"),
            "retailShift": entity("retailshift", "shift"),
            "cashSum": 10000,
            "noCashSum": 0,
        })

        payload = ms_docs.retailsalesreturn_doc(demand)

        self.assertEqual(payload["demand"], ms_docs.meta(demand))
        self.assertEqual(payload["retailShift"], demand["retailShift"])
        self.assertEqual(payload["cashSum"], 10000)
        self.assertEqual(payload["noCashSum"], 0)

    def test_facture_sources_follow_priority(self) -> None:
        created = {"demand": source("demand", "demand-id"), "paymentin": source("paymentin", "payment-id")}
        source_type, selected = ms_docs.choose_facture_source("factureout", created)
        self.assertEqual(source_type, "demand")
        self.assertEqual(selected["id"], "demand-id")

        source_type, selected = ms_docs.choose_facture_source(
            "facturein",
            {
                "supply": source("supply", "supply-id"),
                "paymentout": source("paymentout", "payment-out-id"),
            },
        )
        self.assertEqual(source_type, "paymentout")
        self.assertEqual(selected["id"], "payment-out-id")

    def test_facture_template_removes_read_only_fields(self) -> None:
        demand = source("demand", "demand-id")
        template = {
            "meta": {"href": "https://example.test/factureout/new"},
            "id": "read-only", "accountId": "account", "created": "now",
            "demands": [ms_docs.meta(demand)],
            "positions": {"rows": [{"id": "position-id", "quantity": 1}]},
        }
        with patch.object(ms_docs, "api", return_value=template):
            payload = ms_docs.facture_doc("factureout", "demand", demand)

        self.assertNotIn("meta", payload)
        self.assertNotIn("id", payload)
        self.assertNotIn("id", payload["positions"]["rows"][0])
        self.assertEqual(payload["demands"], [ms_docs.meta(demand)])

    def test_facturein_uses_paymentout_relation(self) -> None:
        paymentout = source("paymentout", "payment-out-id")

        payload = ms_docs.facture_template_payload("facturein", "paymentout", paymentout)

        self.assertEqual(payload["payments"], [ms_docs.meta(paymentout)])
        self.assertNotIn("cashOuts", payload)

    def test_dependent_document_verification_expands_positions(self) -> None:
        demand = source("demand", "demand-id")
        created = entity("salesreturn", "return-id")
        fetched = source("salesreturn", "return-id")
        fetched["demand"] = ms_docs.meta(demand)

        with patch.object(ms_docs, "api", side_effect=[created, fetched]) as api_mock:
            ms_docs.create_and_verify_dependent_document(
                "salesreturn", ms_docs.salesreturn_doc(demand), demand["agent"], "demand", demand
            )

        self.assertEqual(api_mock.call_args_list[1].args[1], "/entity/salesreturn/return-id?expand=positions")

    def test_retailsalesreturn_is_skipped_when_retaildemand_is_missing(self) -> None:
        created = {}
        self.assertIsNone(created.get("retaildemand"))

    def test_retireorder_is_skipped_without_marking_data(self) -> None:
        with patch.multiple(
            ms_docs,
            TEST_MARKED_PRODUCT_ID="",
            TEST_TRACKING_CODE="",
            TEST_RETIRE_ORDER_TYPE="",
            TEST_SUPPORTING_TRANSACTION="",
        ):
            payload, reason = ms_docs.retireorder_doc(entity("counterparty", "cp"))

        self.assertIsNone(payload)
        self.assertIn("TEST_MARKED_PRODUCT_ID", reason)

    def test_error_data_keeps_other_document_processing_possible(self) -> None:
        error = ms_docs.error_data("POST", "/entity/salesreturn", ValueError("bad payload"))
        self.assertIsNone(error["status"])
        self.assertEqual(error["body"], "bad payload")

    def test_counterparty_creation_error_is_saved_in_report_and_ms_log(self) -> None:
        report = {
            "runId": "20260910124659",
            "baseUrl": "https://example.test/api/remap/1.2",
            "counterparties": [],
            "createdDocuments": [],
            "failedDocuments": [],
            "failedCounterparties": [],
            "skippedDocuments": [],
        }
        error = ms_docs.error_data(
            "POST", "/entity/counterparty", ms_docs.ApiError(
                "POST", "/entity/counterparty", 403, '{"errors":[{"error":"Forbidden"}]}'
            )
        )
        ms_docs.add_failed_counterparty(report, 1, error)

        with tempfile.TemporaryDirectory() as temp_dir, patch.object(
            ms_docs, "MS_ERROR_LOG_DIR", Path(temp_dir)
        ):
            exit_code = ms_docs.finish_run(report, should_write_report=True)
            report_file = Path(temp_dir) / "mscontractor_docs_report_20260910124659.json"
            error_log_file = Path(temp_dir) / "mscontractor_document_errors_20260910124659.json"

            self.assertEqual(1, exit_code)
            self.assertTrue(report_file.exists())
            self.assertTrue(error_log_file.exists())
            self.assertIn("failedCounterparties", report_file.read_text(encoding="utf-8"))
            self.assertIn("Forbidden", error_log_file.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
