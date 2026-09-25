import { Document, Message, OfficeBuilding, Phone, Postcard, Tickets, User } from '@element-plus/icons-vue'

/**
 * Metadata for every criterion a duplicate group can be matched by.
 * Only `searchable` fields are accepted by the duplicate search API today;
 * the rest are display-only so future criteria render without UI changes.
 */
export const MATCH_FIELDS = [
  { value: 'name', label: 'Наименование', icon: User, searchable: true },
  { value: 'email', label: 'Email', icon: Message, searchable: true },
  { value: 'phone', label: 'Телефон', icon: Phone, searchable: true },
  { value: 'description', label: 'Описание', icon: Document, searchable: false },
  { value: 'inn', label: 'ИНН', icon: Postcard, searchable: false },
  { value: 'kpp', label: 'КПП', icon: Tickets, searchable: false },
  { value: 'legalTitle', label: 'Юр. наименование', icon: OfficeBuilding, searchable: false }
]

export const SEARCHABLE_MATCH_FIELDS = MATCH_FIELDS.filter((field) => field.searchable)

export function matchFieldMeta(value) {
  return MATCH_FIELDS.find((field) => field.value === value) || { value, label: value || 'Поле', icon: Document }
}

export function matchFieldLabel(value) {
  return matchFieldMeta(value).label
}

/**
 * Returns criteria of a normalized group as `[{ field, label, icon, value }]`.
 * Accepts both the API shape (`matchedBy` + `matchValue`) and the extended
 * shape with a `values` map / `criteria` list.
 */
export function groupCriteria(group) {
  if (!group) {
    return []
  }

  if (Array.isArray(group.criteria) && group.criteria.length) {
    return group.criteria.map((item) => buildCriterion(item.field, item.value))
  }

  const result = []
  if (group.matchedBy) {
    result.push(buildCriterion(group.matchedBy, group.matchValue))
  }

  for (const [field, value] of Object.entries(group.values || {})) {
    if (value && !result.some((item) => item.field === field)) {
      result.push(buildCriterion(field, value))
    }
  }

  return result
}

function buildCriterion(field, value) {
  const meta = matchFieldMeta(field)
  return {
    field,
    label: meta.label,
    icon: meta.icon,
    value: String(value || '')
  }
}
