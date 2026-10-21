import assert from 'node:assert/strict'
import test from 'node:test'

import {
  booleanAttributeOptions,
  buildBooleanAttributeSelection,
  isBooleanAttribute
} from './boolean-attribute.js'

const attribute = { id: 'vip', type: 'boolean' }

test('boolean attributes always offer true and false without an empty option', () => {
  const counterparties = [
    { id: 'main', attributes: [{ id: attribute.id, type: 'boolean', value: null }] },
    { id: 'duplicate', attributes: [] }
  ]

  const options = booleanAttributeOptions(attribute, counterparties)

  assert.deepEqual(options.map((option) => option.value), [true, false])
  assert.ok(options.every((option) => option.clear === false))
})

test('boolean selection preserves an existing primary value or defaults to false when all sources are empty', () => {
  const counterparties = [
    { id: 'main', attributes: [{ id: attribute.id, type: 'boolean', value: null }] },
    { id: 'duplicate', attributes: [{ id: attribute.id, type: 'boolean', value: true }] }
  ]

  assert.deepEqual(
    buildBooleanAttributeSelection(attribute, counterparties, 'main'),
    {
      sourceCounterpartyId: 'duplicate',
      value: true,
      file: null,
      valueJson: 'true',
      fileJson: 'null',
      clear: false
    }
  )

  const emptyCounterparties = [{ id: 'main', attributes: [] }]
  assert.deepEqual(
    buildBooleanAttributeSelection(attribute, emptyCounterparties, 'main'),
    {
      sourceCounterpartyId: 'main',
      value: false,
      file: null,
      valueJson: 'false',
      fileJson: 'null',
      clear: false
    }
  )
})

test('selecting a boolean value records the requested value and rejects other types as boolean metadata', () => {
  const counterparties = [{ id: 'main', attributes: [] }]

  assert.equal(
    buildBooleanAttributeSelection(attribute, counterparties, 'main', true).value,
    true
  )
  assert.equal(isBooleanAttribute({ type: 'Boolean' }), true)
  assert.equal(isBooleanAttribute({ type: 'string' }), false)
})
