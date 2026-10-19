import { documentAction, documentTypeLabel, DOCUMENT_TYPES } from './merge.js'

const OPERATION_LABELS = {
  change_counterparty: 'Перепривязка документов',
  change_agent_and_contract: 'Изменение агента и договора',
  recreate_salesreturn: 'Пересоздание возвратов покупателей',
  recreate_purchasereturn: 'Пересоздание возвратов поставщикам',
  recreate_facturein: 'Пересоздание счетов-фактур полученных',
  recreate_factureout: 'Пересоздание счетов-фактур выданных'
}

const OPERATION_ORDER = Object.keys(OPERATION_LABELS)
const OPERATION_DOCUMENT_TYPES = {
  recreate_salesreturn: 'salesreturn',
  recreate_purchasereturn: 'purchasereturn',
  recreate_facturein: 'facturein',
  recreate_factureout: 'factureout'
}
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

export function moySkladCounterpartyUrl(id) {
  const normalizedId = String(id || '')
  if (!UUID_PATTERN.test(normalizedId)) {
    return ''
  }

  return `https://online.moysklad.ru/app/#company/edit?id=${encodeURIComponent(normalizedId)}`
}

export function moySkladDocumentUrl(type, id) {
  const normalizedType = String(type || '')
  const normalizedId = String(id || '')
  if (!DOCUMENT_TYPES[normalizedType] || !UUID_PATTERN.test(normalizedId)) {
    return ''
  }

  return `https://online.moysklad.ru/app/#${normalizedType}/edit?id=${encodeURIComponent(normalizedId)}`
}

/**
 * Converts stored Egress response JSON into frontend-only groups and document outcomes.
 * The API intentionally returns the original RawJson without interpreting it.
 */
export function normalizeDocumentsInOperation(rows) {
  if (!Array.isArray(rows)) {
    return []
  }

  return rows
    .map((row, rowIndex) => {
      const operationType = String(row?.operationType || '')
      const parsed = parseJson(row?.rawJson)
      const responses = Array.isArray(parsed) ? parsed : parsed && typeof parsed === 'object' ? [parsed] : []
      const attempts = responses.map((response, index) => normalizeAttempt(operationType, response, index))
      if (attempts.length === 0) {
        attempts.push({
          id: `${operationType || 'operation'}-${rowIndex}-empty`,
          statusCode: null,
          errorCode: '',
          message: 'Сохранённый ответ не содержит распознаваемых результатов.',
          documents: []
        })
      }

      const latestByDocument = new Map()
      for (const attempt of attempts) {
        for (const document of attempt.documents) {
          const key = `${document.type}:${document.id || document.newDocumentId || document.errorCode}`
          latestByDocument.set(key, document)
        }
      }
      const latestDocuments = [...latestByDocument.values()]

      return {
        operationType,
        label: OPERATION_LABELS[operationType] || operationType || 'Операция с документами',
        attempts,
        latestDocuments,
        totalCount: latestDocuments.length,
        skippedCount: latestDocuments.filter((document) => document.result === 'skipped').length,
        failedCount: latestDocuments.filter((document) => document.result === 'failed').length
      }
    })
    .sort((left, right) => operationSortKey(left.operationType) - operationSortKey(right.operationType))
}

function normalizeAttempt(operationType, response, index) {
  const body = parseJson(response?.body)
  const responseBody = body && typeof body === 'object' && !Array.isArray(body) ? body : {}
  const documents = collectDocumentOutcomes(operationType, responseBody)
  const bodyMessage = typeof body === 'string'
    ? body
    : firstText(responseBody.message, responseBody.errorMessage, responseBody.error?.message)

  return {
    id: `${operationType || 'operation'}-${index}`,
    statusCode: Number.isFinite(Number(response?.statusCode)) ? Number(response.statusCode) : null,
    errorCode: firstText(responseBody.code, responseBody.errorCode),
    message: bodyMessage,
    documents
  }
}

function collectDocumentOutcomes(operationType, body) {
  const results = []
  const fallbackType = OPERATION_DOCUMENT_TYPES[operationType] || ''
  const isRecreation = operationType.startsWith('recreate_')

  appendRows(results, body.changedDocuments, 'reassigned', fallbackType)
  appendRows(results, body.transferredDocuments, isRecreation ? 'recreated' : 'reassigned', fallbackType)
  appendRows(results, body.recreatedPurchaseReturns, 'recreated', 'purchasereturn')
  appendRows(results, body.recreatedFactureOuts, 'recreated', 'factureout')
  appendRows(results, body.skippedDocuments, 'skipped', fallbackType)
  appendRows(results, body.failures, 'failed', fallbackType)
  appendRows(results, body.createdWithErrors, 'failed', fallbackType)
  appendRows(results, body.failedDocuments, 'failed', fallbackType)
  appendRows(results, body.unCreatedButDeleted, 'failed', fallbackType)

  const representedSkippedIds = new Set(results
    .filter((item) => item.result === 'skipped')
    .map((item) => item.id))
  for (const id of asArray(body.skippedDocumentIds)) {
    const documentId = String(id || '')
    if (documentId && !representedSkippedIds.has(documentId)) {
      results.push(createOutcome({ documentId }, 'skipped', fallbackType))
    }
  }

  const representedTransferredIds = new Set(results
    .filter((item) => item.result === (isRecreation ? 'recreated' : 'reassigned'))
    .map((item) => item.id))
  for (const id of asArray(body.transferredDocumentIds)) {
    const documentId = String(id || '')
    if (documentId && !representedTransferredIds.has(documentId)) {
      results.push(createOutcome({ documentId }, isRecreation ? 'recreated' : 'reassigned', fallbackType))
    }
  }

  const unique = new Map()
  for (const result of results) {
    const key = `${result.result}:${result.id}:${result.newDocumentId}`
    unique.set(key, result)
  }
  return [...unique.values()]
}

function appendRows(target, rows, result, fallbackType) {
  for (const item of asArray(rows)) {
    target.push(createOutcome(item, result, fallbackType))
  }
}

function createOutcome(item, result, fallbackType) {
  const id = firstText(item?.sourceDocumentId, item?.documentId, item?.sourceId, item?.sourcePurchaseReturnId)
  const newDocumentId = firstText(item?.newDocumentId)
  const type = firstText(item?.documentType, item?.type, fallbackType)
  const reason = firstText(item?.reason, item?.error, item?.message, item?.errorMessage)
  const errorCode = firstText(item?.errorCode, item?.code)
  const status = firstText(item?.status)

  return {
    id,
    type,
    typeLabel: documentTypeLabel(type),
    action: documentAction(type),
    result,
    newDocumentId,
    newDocumentUrl: moySkladDocumentUrl(type, newDocumentId),
    status,
    errorCode,
    errorMessage: reason
  }
}

function operationSortKey(type) {
  const index = OPERATION_ORDER.indexOf(type)
  return index === -1 ? OPERATION_ORDER.length : index
}

function parseJson(value) {
  if (typeof value !== 'string') {
    return value
  }

  try {
    return JSON.parse(value)
  } catch {
    return value
  }
}

function asArray(value) {
  return Array.isArray(value) ? value : []
}

function firstText(...values) {
  return values.find((value) => typeof value === 'string' && value.trim())?.trim() || ''
}
