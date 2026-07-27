import { api } from './http'

export function getDuplicateExclusions() {
  return api.get('/api/settings/duplicate-exclusions')
}

export function saveDuplicateExclusions(payload) {
  return api.put('/api/settings/duplicate-exclusions', payload)
}

export function getDuplicateSearchOptions() {
  return api.get('/api/settings/duplicate-search-options')
}

export function saveDuplicateSearchOptions(payload) {
  return api.put('/api/settings/duplicate-search-options', payload)
}

export function getFullSyncTimer() {
  return api.get('/api/settings/full-sync-timer')
}

export function saveFullSyncTimer(payload) {
  return api.put('/api/settings/full-sync-timer', payload)
}
