import assert from 'node:assert/strict'
import test from 'node:test'

import {
  MAX_MERGE_COUNTERPARTIES,
  canStartMerge,
  selectFirstMergeCounterparties,
  toggleMergeCounterpartySelection
} from './merge-selection.js'

test('allows merging between two and fifty selected counterparties', () => {
  assert.equal(canStartMerge([]), false)
  assert.equal(canStartMerge(['one']), false)
  assert.equal(canStartMerge(['one', 'two']), true)
  assert.equal(canStartMerge(Array.from({ length: MAX_MERGE_COUNTERPARTIES }, (_, index) => String(index))), true)
  assert.equal(canStartMerge(Array.from({ length: MAX_MERGE_COUNTERPARTIES + 1 }, (_, index) => String(index))), false)
})

test('prevents selecting a fifty-first counterparty but permits replacing a selection', () => {
  const selected = Array.from({ length: MAX_MERGE_COUNTERPARTIES }, (_, index) => String(index))

  assert.equal(toggleMergeCounterpartySelection(selected, 'extra'), selected)

  const withoutOne = toggleMergeCounterpartySelection(selected, '0')
  assert.equal(withoutOne.length, MAX_MERGE_COUNTERPARTIES - 1)
  assert.deepEqual(toggleMergeCounterpartySelection(withoutOne, 'extra'), [...withoutOne, 'extra'])
})

test('selects the first fifty counterparties in group order', () => {
  const groupIds = Array.from({ length: MAX_MERGE_COUNTERPARTIES + 3 }, (_, index) => String(index))

  assert.deepEqual(
    selectFirstMergeCounterparties(groupIds),
    groupIds.slice(0, MAX_MERGE_COUNTERPARTIES)
  )
})
