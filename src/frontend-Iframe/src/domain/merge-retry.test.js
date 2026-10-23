import assert from 'node:assert/strict'
import test from 'node:test'

import { buildMergeRetryRequest, canRetryMergeJob, normalizeMergeJob } from './merge.js'

const mainCounterpartyId = '10000000-0000-4000-8000-000000000001'
const duplicateCounterpartyId = '20000000-0000-4000-8000-000000000002'
const payload = {
  name: 'Исходный контрагент',
  email: 'company@example.test',
  phone: null,
  description: 'Сохранённое описание',
  attributes: [{ id: '30000000-0000-4000-8000-000000000003', sourceCounterpartyId: duplicateCounterpartyId }]
}

test('reconstructs the original request payload and duplicate IDs from merge state', () => {
  const request = buildMergeRetryRequest({
    mainCounterpartyId,
    payloadVersion: 1,
    payload: JSON.stringify(payload),
    operations: [
      { type: 'discover_documents', counterpartyId: mainCounterpartyId },
      { type: 'archive_duplicate', counterpartyId: duplicateCounterpartyId }
    ]
  })

  assert.deepEqual(request, {
    mainCounterpartyId,
    duplicateCounterpartyIds: [duplicateCounterpartyId],
    mainCounterparty: payload
  })
})

test('does not build retry requests when the payload or duplicate list is incomplete', () => {
  const base = {
    mainCounterpartyId,
    payloadVersion: 1,
    payload: JSON.stringify(payload),
    operations: [{ type: 'archive_duplicate', counterpartyId: duplicateCounterpartyId }]
  }

  assert.equal(buildMergeRetryRequest({ ...base, payload: '{broken' }), null)
  assert.equal(buildMergeRetryRequest({ ...base, operations: [] }), null)
  assert.equal(buildMergeRetryRequest({ ...base, operations: [{ type: 'archive_duplicate', counterpartyId: '' }] }), null)
  assert.equal(buildMergeRetryRequest({ ...base, payloadVersion: 2 }), null)
})

test('rejects retry payloads that cannot be accepted by the merge endpoint', () => {
  const base = {
    mainCounterpartyId,
    payloadVersion: 1,
    operations: [{ type: 'archive_duplicate', counterpartyId: duplicateCounterpartyId }]
  }

  assert.equal(buildMergeRetryRequest({ ...base, payload: JSON.stringify({ name: '  ' }) }), null)
  assert.equal(buildMergeRetryRequest({ ...base, payload: JSON.stringify(payload), mainCounterpartyId: duplicateCounterpartyId }), null)
  assert.equal(buildMergeRetryRequest({ ...base, payload: JSON.stringify(payload), operations: [
    { type: 'archive_duplicate', counterpartyId: duplicateCounterpartyId },
    { type: 'archive_duplicate', counterpartyId: duplicateCounterpartyId }
  ] }), null)
})

test('allows retry only for failed jobs with a reconstructed request', () => {
  const retryRequest = { mainCounterpartyId, duplicateCounterpartyIds: [duplicateCounterpartyId], mainCounterparty: payload }

  assert.equal(canRetryMergeJob({ status: 'failed', retryRequest }), true)
  assert.equal(canRetryMergeJob(normalizeMergeJob({ id: 'failed-job', status: 'failed', retryRequest })), true)
  assert.equal(canRetryMergeJob({ status: 'partial', retryRequest }), false)
  assert.equal(canRetryMergeJob({ status: 'completed', retryRequest }), false)
  assert.equal(canRetryMergeJob({ status: 'failed', retryRequest: null }), false)
})
