import { api } from './http'

export function createMoyskladSession(payload) {
  return api.post('/api/moysklad/session', payload)
}

export function getMoyskladSessionMe() {
  return api.get('/api/moysklad/session/me')
}

export function logoutMoyskladSession() {
  return api.post('/api/moysklad/session/logout')
}
