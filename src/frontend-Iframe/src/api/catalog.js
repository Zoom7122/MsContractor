import { api } from './http'

export function getCatalogState() {
  return api.get('/api/catalog/state')
}

export function getMergeJobDocumentsInOperation(mergeJobId, config = {}) {
  return api.get(
    `/api/catalog/merge-jobs/${encodeURIComponent(mergeJobId)}/documents-in-operation`,
    config
  )
}

export function getCatalogSettings() {
  return api.get('/api/catalog/settings')
}

export function saveCatalogSettings(settings) {
  return api.put('/api/catalog/settings', settings)
}
