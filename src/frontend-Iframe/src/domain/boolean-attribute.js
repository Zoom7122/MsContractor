export function isBooleanAttribute(attribute) {
  return String(attribute?.type || '').toLowerCase() === 'boolean'
}

export function booleanAttributeOptions(attribute, counterparties) {
  return [true, false].map((value) => ({
    key: `boolean-${value}`,
    value,
    valueJson: String(value),
    sources: counterparties.filter((counterparty) => {
      const source = counterparty.attributes?.find((item) => item.id === attribute.id)
      return isBooleanAttribute(source) && source.value === value
    }),
    clear: false
  }))
}

export function buildBooleanAttributeSelection(attribute, counterparties, preferredCounterpartyId, requestedValue) {
  const preferredCounterparty = counterparties.find((item) => item.id === preferredCounterpartyId)
  const preferredValue = preferredCounterparty?.attributes?.find((item) => item.id === attribute.id)
  const matchingSource = typeof requestedValue === 'boolean'
    ? counterparties.find((counterparty) => {
      const source = counterparty.attributes?.find((item) => item.id === attribute.id)
      return isBooleanAttribute(source) && source.value === requestedValue
    })
    : null
  const existingBooleanSource = counterparties.find((counterparty) => {
    const source = counterparty.attributes?.find((item) => item.id === attribute.id)
    return isBooleanAttribute(source) && typeof source.value === 'boolean'
  })
  const selectedSource = typeof requestedValue === 'boolean'
    ? matchingSource || preferredCounterparty
    : typeof preferredValue?.value === 'boolean'
      ? preferredCounterparty
      : existingBooleanSource || preferredCounterparty
  const selectedAttribute = selectedSource?.attributes?.find((item) => item.id === attribute.id)
  const value = typeof requestedValue === 'boolean'
    ? requestedValue
    : typeof selectedAttribute?.value === 'boolean'
      ? selectedAttribute.value
      : false

  return {
    sourceCounterpartyId: selectedSource?.id || preferredCounterpartyId,
    value,
    file: null,
    valueJson: String(value),
    fileJson: 'null',
    clear: false
  }
}
