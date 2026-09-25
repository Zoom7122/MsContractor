import {
  COMPANY_NAMES,
  LONG_COMPANY_NAME,
  LONG_DESCRIPTION,
  LONG_EMAIL,
  createRandom,
  isoMinutesAgo,
  mockGuid,
  nameVariants,
  pick,
  slug
} from './helpers'

/**
 * Builds duplicate groups in the API shape of `POST /api/merge-preview`:
 * `[{ matchedBy, matchValue, counterparties: [...] }]`.
 * Also returns a flat counterparty index used by the selection preview mock.
 */
export function buildDuplicateGroups(scenario) {
  const random = createRandom(scenario === 'many' ? 7 : 11)
  const groupsCount = { empty: 0, many: 64, long: 6, criteria: 5 }[scenario] ?? 8
  const groups = []

  for (let index = 0; index < groupsCount; index += 1) {
    const baseName = scenario === 'long' && index % 2 === 0 ? LONG_COMPANY_NAME : COMPANY_NAMES[index % COMPANY_NAMES.length]
    const matchedBy = ['name', 'email', 'phone'][index % 3]
    const size = scenario === 'many' && index % 9 === 0 ? 12 : 2 + Math.floor(random() * 3)
    const email = scenario === 'long' && index % 2 === 1 ? LONG_EMAIL : `info@${slug(baseName) || 'company'}.ru`
    const phone = `+7 (9${Math.floor(10 + random() * 89)}) ${Math.floor(100 + random() * 899)}-${Math.floor(10 + random() * 89)}-${Math.floor(10 + random() * 89)}`
    const variants = nameVariants(baseName)

    const counterparties = Array.from({ length: size }, (_, position) => {
      const shareName = matchedBy === 'name' || random() > 0.6
      return {
        id: mockGuid(random),
        name: shareName ? baseName : variants[(position + 1) % variants.length],
        email: matchedBy === 'email' || random() > 0.5 ? email : position % 2 ? '' : `sales@${slug(baseName) || 'company'}.ru`,
        phone: matchedBy === 'phone' || random() > 0.5 ? phone : '',
        description: position === 0 && (scenario === 'long' || index === 1) ? LONG_DESCRIPTION : position === 2 ? 'Создан из интернет-магазина' : '',
        archived: position === size - 1 && index % 4 === 3,
        rawJson: {},
        createdAt: isoMinutesAgo(60 * 24 * (30 + position * 7)),
        updatedAt: isoMinutesAgo(60 * (5 + position * 26 + index))
      }
    })

    const matchValue = matchedBy === 'name' ? baseName : matchedBy === 'email' ? email : phone
    for (const item of counterparties) {
      item[matchedBy] = matchValue
    }

    const group = { matchedBy, matchValue, counterparties }

    if (scenario === 'criteria') {
      group.criteria = [
        { field: 'name', value: baseName },
        { field: 'email', value: email },
        { field: 'phone', value: phone },
        { field: 'inn', value: `78${Math.floor(10000000 + random() * 89999999)}` },
        { field: 'kpp', value: `7801${Math.floor(10000 + random() * 89999)}` },
        { field: 'description', value: 'Оптовый покупатель, Санкт-Петербург' },
        { field: 'legalTitle', value: baseName.replace('ООО', 'Общество с ограниченной ответственностью') }
      ].slice(0, 3 + (index % 5))
    }

    groups.push(group)
  }

  return groups
}

export function indexCounterparties(groups) {
  const index = new Map()
  for (const group of groups) {
    for (const item of group.counterparties) {
      index.set(item.id, item)
    }
  }
  return index
}

export function fallbackCounterparty(id, position) {
  const random = createRandom(position + 3)
  const name = pick(random, COMPANY_NAMES)
  return {
    id,
    name,
    email: `info@${slug(name) || 'company'}.ru`,
    phone: '',
    description: '',
    archived: false,
    updatedAt: isoMinutesAgo(120 + position * 30)
  }
}
