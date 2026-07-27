import { api } from './http'

export function getMergeJobs(params) {
  return api.get('/api/merge-jobs', {
    params
  })
}

export function cancelMergeJob(jobId) {
  return api.post(`/api/merge-jobs/${jobId}/cancel`)
}
