import {
  CircleCheckFilled,
  CircleCloseFilled,
  Clock,
  Loading,
  Refresh,
  Remove,
  Switch,
  WarningFilled
} from '@element-plus/icons-vue'

/*
 * Frontend view of the merge domain (DuplicatesMergeService):
 * job statuses, operation types grouped into user-facing stages,
 * MoySklad document types and per-document results.
 */

/** Canonical job statuses → badge presentation. Legacy aliases are mapped in normalizeJobStatus. */
export const JOB_STATUS = {
  pending: { label: 'В очереди', tone: 'neutral', icon: Clock },
  running: { label: 'Выполняется', tone: 'primary', icon: Loading, spinning: true },
  completed: { label: 'Завершено', tone: 'success', icon: CircleCheckFilled },
  partial: { label: 'Выполнено частично', tone: 'warning', icon: WarningFilled },
  failed: { label: 'Ошибка', tone: 'danger', icon: CircleCloseFilled },
  cancelled: { label: 'Отменено', tone: 'neutral', icon: Remove },
  unknown: { label: 'Неизвестно', tone: 'neutral', icon: Clock }
}

const JOB_STATUS_ALIASES = {
  pending: 'pending',
  queued: 'pending',
  accepted: 'pending',
  running: 'running',
  completed: 'completed',
  succeeded: 'completed',
  partially_completed: 'partial',
  partial: 'partial',
  failed: 'failed',
  interrupted: 'failed',
  cancelled: 'cancelled'
}

export function normalizeJobStatus(status) {
  return JOB_STATUS_ALIASES[String(status || '').trim().toLowerCase()] || 'unknown'
}

export function jobStatusMeta(status) {
  return JOB_STATUS[normalizeJobStatus(status)]
}

export function isJobActive(status) {
  const normalized = normalizeJobStatus(status)
  return normalized === 'pending' || normalized === 'running'
}

export function isJobFinished(status) {
  return ['completed', 'partial', 'failed', 'cancelled'].includes(normalizeJobStatus(status))
}

/** Operation / stage status → badge presentation. */
export const STEP_STATUS = {
  pending: { label: 'Ожидает', tone: 'neutral', icon: Clock },
  running: { label: 'Выполняется', tone: 'primary', icon: Loading, spinning: true },
  completed: { label: 'Готово', tone: 'success', icon: CircleCheckFilled },
  partial: { label: 'Частично', tone: 'warning', icon: WarningFilled },
  failed: { label: 'Ошибка', tone: 'danger', icon: CircleCloseFilled },
  skipped: { label: 'Не требуется', tone: 'neutral', icon: Remove }
}

export const OPERATION_TYPES = {
  discover_documents: 'Поиск документов дублей',
  update_main_counterparty: 'Обновление основного контрагента',
  change_document_counterparties: 'Перепривязка документов',
  recreate_salesreturns: 'Пересоздание возвратов покупателей',
  recreate_purchasereturns: 'Пересоздание возвратов поставщикам',
  recreate_factureins: 'Пересоздание счетов-фактур полученных',
  recreate_factureouts: 'Пересоздание счетов-фактур выданных',
  archive_duplicate: 'Архивация дубликата'
}

export function operationLabel(type) {
  return OPERATION_TYPES[type] || type || 'Операция'
}

/** User-facing stages; each groups the backend operations it consists of. */
export const MERGE_STAGES = [
  {
    key: 'prepare',
    label: 'Подготовка',
    hint: 'Поиск документов, связанных с дубликатами',
    operations: ['discover_documents']
  },
  {
    key: 'counterparty',
    label: 'Контрагент',
    hint: 'Запись итоговых полей в основного контрагента',
    operations: ['update_main_counterparty']
  },
  {
    key: 'documents',
    label: 'Документы',
    hint: 'Перепривязка документов к основному контрагенту',
    operations: ['change_document_counterparties']
  },
  {
    key: 'recreate',
    label: 'Пересоздание',
    hint: 'Документы, в которых нельзя сменить контрагента',
    operations: ['recreate_salesreturns', 'recreate_purchasereturns', 'recreate_factureins', 'recreate_factureouts']
  },
  {
    key: 'archive',
    label: 'Архивация',
    hint: 'Перенос дубликатов в архив МоегоСклада',
    operations: ['archive_duplicate']
  },
  {
    key: 'finish',
    label: 'Завершение',
    hint: 'Итог объединения',
    operations: []
  }
]

/**
 * Builds stage rows for a normalized job.
 * Stages without operations are shown only when the job has no operation
 * details yet (e.g. right after a 202 Accepted) so the user sees the plan.
 */
export function buildJobStages(job) {
  const operations = Array.isArray(job?.operations) ? job.operations : []
  const jobStatus = normalizeJobStatus(job?.status)
  const hasOperations = operations.length > 0

  const stages = MERGE_STAGES.map((stage) => {
    if (stage.key === 'finish') {
      return { ...stage, items: [], total: 0, done: 0, failed: 0, status: finishStatus(jobStatus) }
    }

    const items = operations.filter((operation) => stage.operations.includes(operation.type))
    const done = items.filter((item) => item.status === 'completed').length
    const failed = items.filter((item) => item.status === 'failed').length
    const running = items.some((item) => item.status === 'running')

    let status = 'pending'
    if (!items.length) {
      status = hasOperations && isJobFinished(jobStatus) ? 'skipped' : 'pending'
      if (!hasOperations && jobStatus === 'completed') {
        status = 'completed'
      }
    } else if (failed && failed === items.length) {
      status = 'failed'
    } else if (failed) {
      status = 'partial'
    } else if (done === items.length) {
      status = 'completed'
    } else if (running || done > 0) {
      status = 'running'
    }

    return { ...stage, items, total: items.length, done, failed, status }
  })

  if (!hasOperations) {
    return stages
  }

  // Hide stages the job never planned (e.g. no documents to recreate), keep finish.
  return stages.filter((stage) => stage.key === 'finish' || stage.total > 0 || stage.key === 'prepare')
}

function finishStatus(jobStatus) {
  switch (jobStatus) {
    case 'completed':
      return 'completed'
    case 'partial':
      return 'partial'
    case 'failed':
      return 'failed'
    case 'cancelled':
      return 'skipped'
    default:
      return 'pending'
  }
}

export function jobProgress(job) {
  const operations = Array.isArray(job?.operations) ? job.operations : []
  if (!operations.length) {
    return normalizeJobStatus(job?.status) === 'completed' ? 100 : 0
  }

  const finished = operations.filter((item) => item.status === 'completed' || item.status === 'failed').length
  return Math.round((finished / operations.length) * 100)
}

/**
 * MoySklad document types handled by merge.
 * `recreate` types cannot change their counterparty in place, so merge creates a copy.
 */
export const DOCUMENT_TYPES = {
  customerorder: { label: 'Заказ покупателя', action: 'reassign' },
  demand: { label: 'Отгрузка', action: 'reassign' },
  invoiceout: { label: 'Счёт покупателю', action: 'reassign' },
  paymentin: { label: 'Входящий платёж', action: 'reassign' },
  cashin: { label: 'Приходный ордер', action: 'reassign' },
  purchaseorder: { label: 'Заказ поставщику', action: 'reassign' },
  supply: { label: 'Приёмка', action: 'reassign' },
  invoicein: { label: 'Счёт поставщика', action: 'reassign' },
  paymentout: { label: 'Исходящий платёж', action: 'reassign' },
  cashout: { label: 'Расходный ордер', action: 'reassign' },
  contract: { label: 'Договор', action: 'reassign' },
  loss: { label: 'Списание', action: 'reassign' },
  retaildemand: { label: 'Розничная продажа', action: 'reassign' },
  commissionreportin: { label: 'Полученный отчёт комиссионера', action: 'reassign' },
  commissionreportout: { label: 'Выданный отчёт комиссионера', action: 'reassign' },
  salesreturn: { label: 'Возврат покупателя', action: 'recreate' },
  purchasereturn: { label: 'Возврат поставщику', action: 'recreate' },
  facturein: { label: 'Счёт-фактура полученный', action: 'recreate' },
  factureout: { label: 'Счёт-фактура выданный', action: 'recreate' }
}

export function documentTypeLabel(type) {
  return DOCUMENT_TYPES[type]?.label || type || 'Документ'
}

export function documentAction(type) {
  return DOCUMENT_TYPES[type]?.action || 'reassign'
}

export const DOCUMENT_ACTIONS = {
  reassign: { label: 'Перепривязка', hint: 'Контрагент меняется в самом документе', tone: 'primary', icon: Switch },
  recreate: { label: 'Пересоздание', hint: 'Создаётся копия документа на основного контрагента', tone: 'violet', icon: Refresh }
}

/** Per-document merge result. */
export const DOCUMENT_RESULTS = {
  pending: { label: 'Ожидает', tone: 'neutral', icon: Clock },
  reassigned: { label: 'Перепривязан', tone: 'success', icon: CircleCheckFilled },
  recreated: { label: 'Пересоздан', tone: 'success', icon: CircleCheckFilled },
  unchanged: { label: 'Без изменений', tone: 'neutral', icon: Remove },
  skipped: { label: 'Пропущен', tone: 'warning', icon: WarningFilled },
  failed: { label: 'Ошибка', tone: 'danger', icon: CircleCloseFilled }
}

export function documentResultMeta(result) {
  return DOCUMENT_RESULTS[result] || DOCUMENT_RESULTS.pending
}

/** Tolerant normalizer for merge jobs (backend GUID ids and legacy numeric ids). */
export function normalizeMergeJob(item) {
  const operations = Array.isArray(item?.operations) ? item.operations : []
  const documents = Array.isArray(item?.documents) ? item.documents : []
  const secondaryIds = Array.isArray(item?.secondaryCounterpartyIds)
    ? item.secondaryCounterpartyIds
    : Array.isArray(item?.duplicateCounterpartyIds) ? item.duplicateCounterpartyIds : []

  return {
    id: String(item?.id ?? item?.mergeJobId ?? ''),
    kind: String(item?.kind || ''),
    status: normalizeJobStatus(item?.status),
    primaryCounterpartyId: String(item?.primaryCounterpartyId || item?.mainCounterpartyId || ''),
    primaryCounterpartyName: String(item?.primaryCounterpartyName || item?.mainCounterpartyName || ''),
    secondaryCounterpartyIds: secondaryIds.map((value) => String(value || '').trim()).filter(Boolean),
    secondaryCounterparties: Array.isArray(item?.secondaryCounterparties)
      ? item.secondaryCounterparties.map((cp) => ({ id: String(cp?.id || ''), name: String(cp?.name || '') }))
      : [],
    errorMessage: String(item?.errorMessage || ''),
    errorCode: String(item?.errorCode || ''),
    correlationId: String(item?.correlationId || ''),
    createdAt: String(item?.createdAt || ''),
    startedAt: String(item?.startedAt || ''),
    finishedAt: String(item?.finishedAt || item?.completedAt || ''),
    operations: operations.map((operation) => ({
      id: String(operation?.id || ''),
      type: String(operation?.type || operation?.operationType || ''),
      status: String(operation?.status || 'pending'),
      counterpartyId: String(operation?.counterpartyId || ''),
      counterpartyName: String(operation?.counterpartyName || ''),
      attemptCount: Number(operation?.attemptCount || 0),
      errorCode: String(operation?.errorCode || ''),
      errorMessage: String(operation?.errorMessage || ''),
      startedAt: String(operation?.startedAt || ''),
      completedAt: String(operation?.completedAt || '')
    })),
    documents: documents.map(normalizeMergeDocument)
  }
}

export function normalizeMergeDocument(item) {
  const type = String(item?.type || item?.documentType || '')
  return {
    id: String(item?.id || item?.documentId || ''),
    type,
    name: String(item?.name || ''),
    moment: String(item?.moment || ''),
    sum: item?.sum ?? null,
    stateName: String(item?.stateName || ''),
    counterpartyId: String(item?.counterpartyId || ''),
    counterpartyName: String(item?.counterpartyName || ''),
    action: String(item?.action || documentAction(type)),
    result: String(item?.result || 'pending'),
    newDocumentId: String(item?.newDocumentId || ''),
    errorCode: String(item?.errorCode || ''),
    errorMessage: String(item?.errorMessage || item?.error || '')
  }
}
