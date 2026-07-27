import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

const devHost = process.env.VITE_DEV_HOST || '0.0.0.0'
const previewHost = process.env.VITE_PREVIEW_HOST || '0.0.0.0'
const backendProxyTarget = process.env.VITE_BACKEND_PROXY_TARGET || 'http://127.0.0.1:18080'
const allowedHosts = parseAllowedHosts(process.env.VITE_ALLOWED_HOSTS)

const backendProxy = {
  target: backendProxyTarget,
  changeOrigin: true,
  timeout: 1000000,
  proxyTimeout: 1000000
}

export default defineConfig({
  base: '/',
  plugins: [vue()],

  server: {
    host: devHost,
    port: 5174,
    allowedHosts,
    proxy: {
      '/api': backendProxy,
      '/health': backendProxy
    }
  },

  preview: {
    host: previewHost,
    port: 4174,
    allowedHosts,
    proxy: {
      '/api': backendProxy,
      '/health': backendProxy
    }
  }
})


function parseAllowedHosts(rawValue) {
  const configuredHosts = String(rawValue || '')
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)

  if (configuredHosts.length > 0) {
    return configuredHosts
  }

  return ['127.0.0.1', 'localhost']
}
