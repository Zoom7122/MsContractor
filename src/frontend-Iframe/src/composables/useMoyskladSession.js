import { computed, ref } from 'vue'

import {
  createMoyskladSession,
  getMoyskladSessionMe,
  logoutMoyskladSession
} from '../api/moyskladSession'

const session = ref(null)
const loading = ref(false)
const error = ref(null)

const isAuthenticated = computed(() => Boolean(session.value))

export function useMoyskladSession() {
  async function initMoyskladSession() {
    loading.value = true
    error.value = null

    try {
      const params = new URLSearchParams(window.location.search)

      const contextKey = params.get('contextKey')
      const appId = params.get('appId')
      const appUid = params.get('appUid')
      const userLocale = params.get('userLocale')

      if (contextKey) {
        session.value = await createMoyskladSession({
          contextKey,
          appId,
          appUid,
          userLocale
        })
        removeMoyskladContextFromUrl()

        return session.value
      }

      session.value = await getMoyskladSessionMe()
      return session.value
    } catch (e) {
      const params = new URLSearchParams(window.location.search)
      const fallbackMessage = params.has('contextKey')
        ? 'Не удалось создать iframe-сессию'
        : 'Откройте решение из МоегоСклада'

      error.value = e.message || fallbackMessage
      if (!params.has('contextKey')) {
        error.value = fallbackMessage
      }
      session.value = null
      throw e
    } finally {
      loading.value = false
    }
  }

  async function loadCurrentMoyskladSession() {
    loading.value = true
    error.value = null

    try {
      session.value = await getMoyskladSessionMe()
      return session.value
    } catch (e) {
      error.value = e.message || 'Нет активной iframe-сессии'
      session.value = null
      throw e
    } finally {
      loading.value = false
    }
  }

  async function logout() {
    await logoutMoyskladSession()
    session.value = null
  }

  return {
    session,
    loading,
    error,
    isAuthenticated,
    initMoyskladSession,
    loadCurrentMoyskladSession,
    logout
  }
}

function removeMoyskladContextFromUrl() {
  const url = new URL(window.location.href)
  for (const parameter of ['contextKey', 'appId', 'appUid', 'userLocale']) {
    url.searchParams.delete(parameter)
  }

  const cleanUrl = `${url.pathname}${url.search}${url.hash}`
  window.history.replaceState(window.history.state, '', cleanUrl)
}
