import axios from 'axios'

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || ''

export const api = axios.create({
  baseURL: API_BASE_URL,
  withCredentials: true,
  headers: {
    'Content-Type': 'application/json'
  }
})

api.interceptors.request.use((config) => {
  const baseURL = String(config.baseURL || '').replace(/\/$/, '')
  const url = String(config.url || '')

  if (baseURL.endsWith('/api') && (url === '/api' || url.startsWith('/api/'))) {
    config.url = url.slice('/api'.length) || '/'
  }

  return config
})

api.interceptors.response.use(
  (response) => response.data,
  (error) => {
    const message =
      error.response?.data?.error ||
      error.response?.data?.message ||
      error.message ||
      'Ошибка запроса'

    const normalizedError = new Error(message)
    normalizedError.status = error.response?.status
    normalizedError.response = error.response

    return Promise.reject(normalizedError)
  }
)
