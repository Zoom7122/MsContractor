import { api } from './http'

export function getDuplicateGroups(params) {
  return api.get('/api/duplicates', {
    params
  })
}

export function getDuplicateSearch(params) {
  return api.get('/api/duplicates/search', {
    params
  })
}

export function getDuplicateMergePreview(counterpartyIds) {
  return api.post('/api/duplicates/merge-preview', {
    counterpartyIds
  })
}

export function mergeDuplicates(payload) {
  return api.post('/api/duplicates/merge', payload)
}

export function mergeMultipleDuplicates(payload) {
  return api.post('/api/duplicates/merge-multiple', payload)
}
