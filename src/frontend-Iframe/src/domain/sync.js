export const SYNC_PAGE_SIZE = 25

export function syncPage(value) {
  const page = Number(value)
  return Number.isSafeInteger(page) && page >= 1 && (page - 1) * SYNC_PAGE_SIZE <= 2147483647 ? page : 1
}

export function syncRunIsActive(run) {
  return ['queued', 'pending', 'accepted', 'running'].includes(String(run?.status || '').toLowerCase())
}

export function syncRunStatus(run) {
  const status = String(run?.status || '').toLowerCase()
  if (status === 'completed') {
    return Number(run?.errorsCount) > 0
      ? { label: 'Завершена с ошибками', tone: 'warning' }
      : { label: 'Завершена', tone: 'success' }
  }
  if (status === 'failed') return { label: 'Ошибка', tone: 'danger' }
  if (status === 'running') return { label: 'Выполняется', tone: 'primary', spinning: true }
  if (syncRunIsActive(run)) return { label: 'В очереди', tone: 'neutral' }
  return { label: 'Нет данных', tone: 'neutral' }
}

export function syncModeLabel(run) {
  return run?.executionMode === 'incremental' ? 'Инкрементальная' : 'Полная'
}
