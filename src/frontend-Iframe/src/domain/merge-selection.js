export const MAX_MERGE_COUNTERPARTIES = 50

export function canStartMerge(counterpartyIds) {
  return counterpartyIds.length >= 2 && counterpartyIds.length <= MAX_MERGE_COUNTERPARTIES
}

export function selectFirstMergeCounterparties(counterpartyIds) {
  return counterpartyIds.slice(0, MAX_MERGE_COUNTERPARTIES)
}

export function toggleMergeCounterpartySelection(selectedIds, counterpartyId) {
  if (selectedIds.includes(counterpartyId)) {
    return selectedIds.filter((id) => id !== counterpartyId)
  }

  if (selectedIds.length >= MAX_MERGE_COUNTERPARTIES) {
    return selectedIds
  }

  return [...selectedIds, counterpartyId]
}
