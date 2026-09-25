// Deterministic helpers so mock screens look the same on every reload.

export function createRandom(seed = 42) {
  let state = seed >>> 0
  return function random() {
    state = (state * 1664525 + 1013904223) >>> 0
    return state / 4294967296
  }
}

export function pick(random, items) {
  return items[Math.floor(random() * items.length) % items.length]
}

export function mockGuid(random) {
  const hex = () => Math.floor(random() * 16).toString(16)
  const block = (length) => Array.from({ length }, hex).join('')
  return `${block(8)}-${block(4)}-4${block(3)}-a${block(3)}-${block(12)}`
}

export function isoMinutesAgo(minutes) {
  return new Date(Date.now() - minutes * 60_000).toISOString()
}

export const LONG_COMPANY_NAME =
  'Общество с ограниченной ответственностью «Торгово-промышленная компания Северо-Западный региональный дистрибьюторский центр оптовой и розничной торговли строительными материалами»'

export const LONG_EMAIL = 'departament.zakupok.i.logistiki.severo-zapadnyi-filial@torgovo-promyshlennaya-kompaniya.example.ru'

export const LONG_DESCRIPTION =
  'Крупный оптовый покупатель. Работает по предоплате 50%, отгрузки только со склада «Центральный». Контактное лицо — Петрова Анна Сергеевна, бухгалтерия запрашивает закрывающие документы до 5 числа месяца. Не путать с одноимённой компанией из Твери.'

export const COMPANY_NAMES = [
  'ООО «Ромашка»',
  'ИП Иванов Сергей Петрович',
  'ООО «Вектор Плюс»',
  'АО «СтройМаркет»',
  'ООО «Альфа-Трейд»',
  'ИП Кузнецова Мария Андреевна',
  'ООО «ТехноСнаб»',
  'ООО «Северная логистика»',
  'ЗАО «Меридиан»',
  'ООО «Фабрика вкуса»',
  'ИП Смирнов А. В.',
  'ООО «Горизонт»'
]

export function nameVariants(name) {
  const bare = name.replace(/^(ООО|АО|ЗАО|ИП)\s+/, '').replace(/[«»]/g, '')
  const prefix = name.match(/^(ООО|АО|ЗАО|ИП)/)?.[0] || 'ООО'
  return [name, `${bare} ${prefix}`, `${prefix} ${bare}`, `${name} (дубль)`, `${name.toUpperCase()}`]
}

export function slug(value) {
  const map = {
    а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y', к: 'k', л: 'l',
    м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'h', ц: 'c', ч: 'ch', ш: 'sh',
    щ: 'sch', ы: 'y', э: 'e', ю: 'yu', я: 'ya'
  }
  return value
    .toLowerCase()
    .replace(/[а-яё]/g, (char) => map[char] || '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '')
    .slice(0, 24)
}
