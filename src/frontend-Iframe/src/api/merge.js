import { api } from './http'

export function createMergeJob(request) {
  return api.post('/api/merge-jobs', request)
}
