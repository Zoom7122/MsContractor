import { api } from './http'

export function getCatalogState() {
  return api.get('/api/catalog/state')
}

export function getCatalogSettings() {
  return api.get('/api/catalog/settings')
}

export function saveCatalogSettings(settings) {
  return api.put('/api/catalog/settings', settings)
}
