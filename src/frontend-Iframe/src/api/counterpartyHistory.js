import { api } from './http'

export function getCounterpartyChangeHistory(params) {
  return api.get('/api/counterparties/change-history', {
    params
  })
}
