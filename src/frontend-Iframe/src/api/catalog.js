import { api } from './http'

export function getCatalogState() {
  return api.get('/api/catalog/state')
}
