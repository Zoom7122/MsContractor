import { api } from './http'

export function startFullSync() {
  return api.post('/api/sync')
}

export function startIncrementalSync() {
  return api.post('/api/sync/incremental')
}
