import { api } from './http'

export function getCounterpartyPage(counterpartyId) {
  return api.get(`/api/counterparties/${counterpartyId}`)
}

export function runCounterpartyFullSync(counterpartyId) {
  return api.post(`/api/counterparties/${counterpartyId}/full-sync`)
}

export function getCounterpartyAttributes() {
  return api.get('/api/counterparties/attributes')
}

export function saveCounterpartyAttributeMergeSettings(payload) {
  return api.put('/api/counterparties/attributes/merge-settings', payload)
}
