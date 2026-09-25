const CODE_MESSAGES = {
  SESSION_UNAUTHORIZED: 'Сессия истекла. Откройте решение заново из МоегоСклада.',
  COUNTERPARTY_BUSY: 'Один из выбранных контрагентов уже участвует в другом объединении. Дождитесь его завершения и обновите поиск.',
  INVALID_DUPLICATE_FIELDS: 'Выберите хотя бы одно поле для поиска дублей.',
  DUPLICATE_PREVIEW_UNAVAILABLE: 'Сервис поиска дублей временно недоступен. Повторите попытку через минуту.',
  CATALOG_SYNC_UNAVAILABLE: 'Очередь синхронизации временно недоступна. Повторите попытку позже.'
}

/**
 * Splits a request error into a readable message and secondary technical details.
 * Technical data (HTTP status, code, correlation id, raw text) is never the headline.
 */
export function toUserError(error, fallbackMessage = 'Не удалось выполнить запрос') {
  if (!error) {
    return null
  }

  if (typeof error === 'string') {
    return { message: error, details: [] }
  }

  const status = error.status ?? error.response?.status
  const data = error.response?.data
  const code = typeof data?.code === 'string' ? data.code : ''
  const correlationId =
    error.response?.headers?.['x-correlation-id'] ||
    data?.correlationId ||
    ''
  const rawMessage = String(error.message || '').trim()

  let message = CODE_MESSAGES[code]
  if (!message && status === 401) {
    message = CODE_MESSAGES.SESSION_UNAUTHORIZED
  }
  if (!message && status === 503) {
    message = 'Сервис временно недоступен. Повторите попытку позже.'
  }
  if (!message && !status && /network/i.test(rawMessage)) {
    message = 'Нет связи с сервером. Проверьте подключение и повторите попытку.'
  }
  if (!message) {
    message = fallbackMessage
  }

  const details = [
    status ? { label: 'HTTP', value: String(status) } : null,
    code ? { label: 'Код', value: code } : null,
    correlationId ? { label: 'Correlation ID', value: String(correlationId) } : null,
    rawMessage && rawMessage !== message ? { label: 'Ответ сервера', value: rawMessage } : null
  ].filter(Boolean)

  return { message, details, code, status }
}

/** Reads the id and status of a `202 Accepted` payload (`{ mergeJobId | syncRunId, status }`). */
export function readAccepted(response) {
  if (!response || typeof response !== 'object') {
    return { id: '', status: 'accepted' }
  }

  const id = response.mergeJobId ?? response.syncRunId ?? response.operationId ?? response.id ?? ''
  return {
    id: String(id || ''),
    status: String(response.status || 'accepted')
  }
}
