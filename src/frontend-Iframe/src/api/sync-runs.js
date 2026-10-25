import { api } from './http'

export function getSyncRuns(page = 1, pageSize = 25, signal) {
  return api.get('/api/sync-runs', { params: { page, pageSize }, signal })
}

export function getSyncRunErrors(syncRunId, page = 1, pageSize = 25, signal) {
  return api.get(`/api/sync-runs/${encodeURIComponent(syncRunId)}/errors`, {
    params: { page, pageSize }, signal
  })
}
