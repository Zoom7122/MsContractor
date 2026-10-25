import { onScopeDispose, ref, shallowRef, watch } from 'vue'

// A request belongs to one route snapshot; polling must not overwrite a newer page.
export function useSyncRunPage(source, fetchPage, isActive) {
  const data = shallowRef(null)
  const error = shallowRef(null)
  const loading = ref(false)
  let timer
  let controller
  let requestId = 0
  let disposed = false

  async function reload() {
    window.clearTimeout(timer)
    controller?.abort()
    controller = new AbortController()
    const currentController = controller
    const currentRequestId = ++requestId
    loading.value = true
    error.value = null
    try {
      const response = await fetchPage(currentController.signal)
      if (!disposed && currentRequestId === requestId) data.value = response
    } catch (caught) {
      if (!disposed && currentRequestId === requestId && !currentController.signal.aborted) error.value = caught
    } finally {
      if (!disposed && currentRequestId === requestId) {
        loading.value = false
        if (isActive(data.value)) timer = window.setTimeout(reload, 10000)
      }
    }
  }

  watch(source, () => {
    data.value = null
    void reload()
  }, { immediate: true })

  onScopeDispose(() => {
    disposed = true
    requestId++
    controller?.abort()
    window.clearTimeout(timer)
  })

  return { data, error, loading, reload }
}
