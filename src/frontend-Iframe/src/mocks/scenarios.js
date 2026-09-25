export const MOCK_SCENARIOS = [
  { value: 'normal', label: 'Обычные данные' },
  { value: 'empty', label: 'Пусто' },
  { value: 'loading', label: 'Долгая загрузка' },
  { value: 'error', label: 'Ошибки сервера' },
  { value: 'many', label: 'Много данных' },
  { value: 'long', label: 'Длинные строки' },
  { value: 'criteria', label: 'Много критериев дублей' },
  { value: 'partial', label: 'Частичные ошибки merge' }
]

const STORAGE_KEY = 'mscontractor.ui-mock-scenario'

export function readMockScenario() {
  const fromQuery = new URLSearchParams(window.location.search).get('mock')
  if (fromQuery && MOCK_SCENARIOS.some((item) => item.value === fromQuery)) {
    writeMockScenario(fromQuery)
    return fromQuery
  }

  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    if (stored && MOCK_SCENARIOS.some((item) => item.value === stored)) {
      return stored
    }
  } catch {
    // Storage can be unavailable inside a sandboxed iframe.
  }

  return 'normal'
}

export function writeMockScenario(value) {
  try {
    window.localStorage.setItem(STORAGE_KEY, value)
  } catch {
    // Ignore: the scenario then lives only in the URL.
  }
}
