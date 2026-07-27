import { api } from './http'

export function getDashboardStatus() {
  return api.get('/api/dashboard.status')
}

export function startSync(mode = 'incremental') {
  return api.post('/api/sync', null, {
    params: {
      mode,
    },
  })
}

export function cancelSync() {
  return api.post('/api/sync/cancel')
}
