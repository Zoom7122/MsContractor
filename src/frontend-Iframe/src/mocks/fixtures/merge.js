import { COMPANY_NAMES, createRandom, isoMinutesAgo, mockGuid, pick } from './helpers'

const DOCUMENT_TYPE_WEIGHTS = [
  ['demand', 26],
  ['paymentin', 18],
  ['customerorder', 12],
  ['invoiceout', 8],
  ['supply', 6],
  ['paymentout', 5],
  ['cashin', 4],
  ['contract', 3],
  ['salesreturn', 6],
  ['purchasereturn', 4],
  ['facturein', 4],
  ['factureout', 4]
]

const RECREATE_TYPES = new Set(['salesreturn', 'purchasereturn', 'facturein', 'factureout'])
const STATES = ['Новый', 'Подтверждён', 'Отгружен', 'Оплачен', 'Закрыт', '']

function weightedType(random) {
  const total = DOCUMENT_TYPE_WEIGHTS.reduce((sum, [, weight]) => sum + weight, 0)
  let roll = random() * total
  for (const [type, weight] of DOCUMENT_TYPE_WEIGHTS) {
    roll -= weight
    if (roll <= 0) {
      return type
    }
  }
  return 'demand'
}

/**
 * Documents linked to duplicate counterparties.
 * `withResults` adds per-document merge results for the job details drawer.
 */
export function buildMergeDocuments(duplicates, { count = 18, seed = 5, outcome = 'none' } = {}) {
  const random = createRandom(seed)
  const documents = []

  for (let index = 0; index < count; index += 1) {
    const owner = duplicates[index % Math.max(duplicates.length, 1)] || { id: '', name: 'Дубликат' }
    const type = weightedType(random)
    const action = RECREATE_TYPES.has(type) ? 'recreate' : 'reassign'
    const document = {
      id: mockGuid(random),
      type,
      name: String(100 + Math.floor(random() * 9000)).padStart(5, '0'),
      moment: isoMinutesAgo(60 * 24 * Math.floor(1 + random() * 300)),
      sum: type === 'contract' ? null : Math.round(random() * 450_000) * 100,
      stateName: pick(random, STATES),
      counterpartyId: owner.id,
      counterpartyName: owner.name,
      action
    }

    if (outcome !== 'none') {
      Object.assign(document, documentOutcome(outcome, action, index, random))
    }

    documents.push(document)
  }

  return documents
}

function documentOutcome(outcome, action, index, random) {
  if (outcome === 'running') {
    if (action === 'recreate') {
      return { result: 'pending' }
    }
    return { result: index % 3 === 0 ? 'pending' : 'reassigned' }
  }

  if (outcome === 'partial') {
    if (action === 'recreate' && index % 3 === 0) {
      return {
        result: 'failed',
        errorCode: 'MOYSKLAD_VALIDATION',
        errorMessage: 'МойСклад отклонил создание копии: документ-основание закрыт для изменений.'
      }
    }
    if (index % 7 === 0) {
      return { result: 'skipped', errorMessage: 'Документ уже принадлежит основному контрагенту.' }
    }
  }

  if (outcome === 'failed') {
    return { result: 'pending' }
  }

  return {
    result: action === 'recreate' ? 'recreated' : index % 11 === 5 ? 'unchanged' : 'reassigned',
    newDocumentId: action === 'recreate' ? mockGuid(random) : ''
  }
}

function operation(type, status, extra = {}) {
  return {
    id: `${type}-${Math.random().toString(16).slice(2, 10)}`,
    type,
    status,
    attemptCount: status === 'pending' ? 0 : 1,
    ...extra
  }
}

function makeParticipants(random, duplicatesCount) {
  const baseName = pick(random, COMPANY_NAMES)
  const primary = { id: mockGuid(random), name: baseName }
  const duplicates = Array.from({ length: duplicatesCount }, (_, index) => ({
    id: mockGuid(random),
    name: index === 0 ? `${baseName} (дубль)` : `${baseName.replace(/[«»]/g, '')} ${index + 1}`
  }))
  return { primary, duplicates }
}

function buildJob(kind, random, minutesAgo) {
  const duplicatesCount = kind === 'partial' ? 3 : kind === 'pending' ? 1 : 2
  const { primary, duplicates } = makeParticipants(random, duplicatesCount)
  const base = {
    id: mockGuid(random),
    correlationId: mockGuid(random),
    primaryCounterpartyId: primary.id,
    primaryCounterpartyName: primary.name,
    secondaryCounterpartyIds: duplicates.map((item) => item.id),
    secondaryCounterparties: duplicates,
    createdAt: isoMinutesAgo(minutesAgo),
    startedAt: kind === 'pending' ? '' : isoMinutesAgo(minutesAgo - 1),
    finishedAt: ['completed', 'partial', 'failed'].includes(kind) ? isoMinutesAgo(Math.max(minutesAgo - 4, 0)) : ''
  }

  const archive = (status, extra) => duplicates.map((item) =>
    operation('archive_duplicate', status, { counterpartyId: item.id, counterpartyName: item.name, ...extra })
  )

  switch (kind) {
    case 'pending':
      return { ...base, status: 'pending', operations: [], documents: [] }

    case 'running':
      return {
        ...base,
        status: 'running',
        operations: [
          operation('discover_documents', 'completed'),
          operation('update_main_counterparty', 'completed'),
          operation('change_document_counterparties', 'running'),
          operation('recreate_salesreturns', 'pending'),
          ...archive('pending')
        ],
        documents: buildMergeDocuments(duplicates, { count: 14, seed: 21, outcome: 'running' })
      }

    case 'partial': {
      const archiveOps = archive('completed')
      archiveOps[2] = {
        ...archiveOps[2],
        status: 'failed',
        attemptCount: 5,
        errorCode: 'EGRESS_RETRY_EXHAUSTED',
        errorMessage: 'The Egress retry policy was exhausted. MoySklad returned 412: Entity is locked by another operation.'
      }
      return {
        ...base,
        status: 'partially_completed',
        operations: [
          operation('discover_documents', 'completed'),
          operation('update_main_counterparty', 'completed'),
          operation('change_document_counterparties', 'completed'),
          operation('recreate_salesreturns', 'completed'),
          operation('recreate_factureins', 'failed', {
            attemptCount: 1,
            errorCode: 'MOYSKLAD_VALIDATION',
            errorMessage: 'МойСклад отклонил создание счёта-фактуры: документ-основание закрыт для изменений.'
          }),
          ...archiveOps
        ],
        documents: buildMergeDocuments(duplicates, { count: 26, seed: 33, outcome: 'partial' })
      }
    }

    case 'failed':
      return {
        ...base,
        status: 'failed',
        errorMessage: 'Не удалось обновить основного контрагента.',
        operations: [
          operation('discover_documents', 'completed'),
          operation('update_main_counterparty', 'failed', {
            attemptCount: 1,
            errorCode: 'MOYSKLAD_VALIDATION',
            errorMessage: 'MoySklad returned 400: Поле «phone» имеет неверный формат.'
          }),
          operation('change_document_counterparties', 'pending'),
          ...archive('pending')
        ],
        documents: buildMergeDocuments(duplicates, { count: 8, seed: 44, outcome: 'failed' })
      }

    default:
      return {
        ...base,
        status: 'completed',
        operations: [
          operation('discover_documents', 'completed'),
          operation('update_main_counterparty', 'completed'),
          operation('change_document_counterparties', 'completed'),
          operation('recreate_purchasereturns', 'completed'),
          ...archive('completed')
        ],
        documents: buildMergeDocuments(duplicates, { count: 12, seed: 55, outcome: 'completed' })
      }
  }
}

export function buildMergeJobs(scenario) {
  if (scenario === 'empty') {
    return []
  }

  const random = createRandom(scenario === 'many' ? 91 : 17)
  const plan =
    scenario === 'many'
      ? Array.from({ length: 28 }, (_, index) => ['completed', 'completed', 'partial', 'running', 'failed', 'pending', 'completed'][index % 7])
      : scenario === 'partial'
        ? ['partial', 'partial', 'running', 'completed']
        : ['running', 'pending', 'partial', 'completed', 'failed']

  return plan.map((kind, index) => buildJob(kind, random, 3 + index * 17))
}

export function buildBusyCounterpartyIds(jobs) {
  return jobs
    .filter((job) => job.status === 'running' || job.status === 'pending')
    .flatMap((job) => [job.primaryCounterpartyId, ...job.secondaryCounterpartyIds])
}
