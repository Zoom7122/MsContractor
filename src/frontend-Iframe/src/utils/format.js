const dateTimeFormatter = new Intl.DateTimeFormat('ru-RU', {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit'
})

const timeFormatter = new Intl.DateTimeFormat('ru-RU', {
  hour: '2-digit',
  minute: '2-digit'
})

const numberFormatter = new Intl.NumberFormat('ru-RU')

export function displayValue(value, fallback = '—') {
  if (value === null || value === undefined) {
    return fallback
  }

  const normalized = typeof value === 'string' ? value.trim() : value
  return normalized === '' ? fallback : normalized
}

export function formatDateTime(value, fallback = '—') {
  const date = parseDate(value)
  if (!date) {
    return value ? String(value) : fallback
  }

  return dateTimeFormatter.format(date).replace(',', '')
}

/** Short form for lists: time only for today, date + time otherwise. */
export function formatDateTimeShort(value, fallback = '—') {
  const date = parseDate(value)
  if (!date) {
    return value ? String(value) : fallback
  }

  const now = new Date()
  const sameDay =
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate()

  return sameDay ? `сегодня ${timeFormatter.format(date)}` : formatDateTime(date)
}

export function formatNumber(value, fallback = '0') {
  const numeric = Number(value)
  return Number.isFinite(numeric) ? numberFormatter.format(numeric) : fallback
}

/** pluralize(3, ['контрагент', 'контрагента', 'контрагентов']) → 'контрагента' */
export function pluralize(count, forms) {
  const value = Math.abs(Number(count) || 0)
  const mod10 = value % 10
  const mod100 = value % 100

  if (mod10 === 1 && mod100 !== 11) {
    return forms[0]
  }
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) {
    return forms[1]
  }
  return forms[2]
}

export function countLabel(count, forms) {
  return `${formatNumber(count)} ${pluralize(count, forms)}`
}

export const COUNTERPARTY_FORMS = ['контрагент', 'контрагента', 'контрагентов']
export const DUPLICATE_FORMS = ['дубликат', 'дубликата', 'дубликатов']
export const DOCUMENT_FORMS = ['документ', 'документа', 'документов']
export const GROUP_FORMS = ['группа', 'группы', 'групп']

/** First block of a GUID (or the whole id when it is short) for compact display. */
export function shortId(value) {
  const normalized = String(value || '').trim()
  if (!normalized) {
    return ''
  }

  const [head] = normalized.split('-')
  return head.length >= 6 && normalized.length > 12 ? head : normalized
}

function parseDate(value) {
  if (!value) {
    return null
  }

  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}
