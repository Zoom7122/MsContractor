export function normalizeQueryValue(rawValue) {
  if (Array.isArray(rawValue)) {
    return String(rawValue[0] || '').trim()
  }

  return String(rawValue || '').trim()
}

/** Reads `a,b` / `['a', 'b,c']` query values into a unique ordered list. */
export function parseDelimitedQuery(rawValue, allowedValues = null) {
  const values = Array.isArray(rawValue) ? rawValue : [rawValue]
  const allowed = allowedValues ? new Set(allowedValues) : null
  const result = []

  for (const value of values) {
    const parts = String(value || '')
      .split(',')
      .map((item) => item.trim())
      .filter(Boolean)

    for (const part of parts) {
      if ((allowed && !allowed.has(part)) || result.includes(part)) {
        continue
      }

      result.push(part)
    }
  }

  return result
}
