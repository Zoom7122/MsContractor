import assert from 'node:assert/strict'
import test from 'node:test'

import { moySkladCounterpartyUrl, moySkladDocumentUrl, normalizeDocumentsInOperation } from './documents-in-operation.js'

test('normalizes skipped documents and their reasons from stored responses', () => {
  const groups = normalizeDocumentsInOperation([{
    operationType: 'change_counterparty',
    rawJson: JSON.stringify([{
      statusCode: 207,
      body: {
        skippedDocuments: [{ documentType: 'demand', documentId: 'source-id', reason: 'DOCUMENT_TYPE_NOT_CONFIGURED' }]
      }
    }])
  }])

  assert.equal(groups.length, 1)
  assert.equal(groups[0].skippedCount, 1)
  assert.equal(groups[0].attempts[0].statusCode, 207)
  assert.equal(groups[0].attempts[0].documents[0].result, 'skipped')
  assert.equal(groups[0].attempts[0].documents[0].errorMessage, 'DOCUMENT_TYPE_NOT_CONFIGURED')
})

test('links recreated document ids using their document type', () => {
  const newDocumentId = '123e4567-e89b-42d3-a456-426614174000'
  const groups = normalizeDocumentsInOperation([{
    operationType: 'recreate_salesreturn',
    rawJson: JSON.stringify([{
      statusCode: 200,
      body: {
        transferredDocuments: [{ sourceDocumentId: 'source-id', newDocumentId }]
      }
    }])
  }])

  assert.equal(
    groups[0].attempts[0].documents[0].newDocumentUrl,
    `https://online.moysklad.ru/app/#salesreturn/edit?id=${newDocumentId}`
  )
  assert.equal(
    moySkladDocumentUrl('unknown', newDocumentId),
    ''
  )
  assert.equal(
    moySkladDocumentUrl('salesreturn', 'not-a-guid'),
    ''
  )
})

test('uses factureout type for related documents returned during purchase return recreation', () => {
  const newDocumentId = '123e4567-e89b-42d3-a456-426614174001'
  const groups = normalizeDocumentsInOperation([{
    operationType: 'recreate_purchasereturn',
    rawJson: JSON.stringify([{
      statusCode: 200,
      body: {
        recreatedFactureOuts: [{ sourceDocumentId: 'invoice-source', newDocumentId }]
      }
    }])
  }])

  assert.equal(groups[0].attempts[0].documents[0].type, 'factureout')
  assert.equal(
    groups[0].attempts[0].documents[0].newDocumentUrl,
    `https://online.moysklad.ru/app/#factureout/edit?id=${newDocumentId}`
  )
})

test('primary counterparty links point to the MoySklad company card', () => {
  const id = '8e492f66-5294-4aab-8bcf-e7f0a4ddcb1a'
  assert.equal(
    moySkladCounterpartyUrl(id),
    `https://online.moysklad.ru/app/#company/edit?id=${id}`
  )
  assert.equal(moySkladCounterpartyUrl('invalid'), '')
})
