import {
  COMPANY_NAMES,
  LONG_COMPANY_NAME,
  LONG_DESCRIPTION,
  LONG_EMAIL,
  createRandom,
  isoMinutesAgo,
  mockGuid,
  pick
} from './helpers'
import { buildMergeDocuments } from './merge'

export function buildOverview(scenario, jobs) {
  if (scenario === 'empty') {
    return {
      overviewData: {
        connection: { ok: true, label: 'Подключено', description: 'Контрагентов в МС: 0', counterpartyTotal: 0 },
        local: { counterpartiesCount: 0 },
        duplicates: { groupsCount: 0 },
        mergeQueue: { jobsCount: 0 },
        lastSync: { startedAtLabel: null, modeLabel: 'Синхронизация ещё не запускалась' }
      },
      dashboardStatusData: { totalCounterparties: 0, processedCounterparties: 0, running: false, status: 'idle' }
    }
  }

  const total = scenario === 'many' ? 184_512 : 1356
  const local = scenario === 'many' ? 131_020 : 978
  const activeJobs = jobs.filter((job) => job.status === 'running' || job.status === 'pending').length

  return {
    overviewData: {
      connection: { ok: true, label: 'Подключено', description: null, counterpartyTotal: total },
      local: { counterpartiesCount: local },
      duplicates: { groupsCount: scenario === 'many' ? 64 : 8 },
      mergeQueue: { jobsCount: activeJobs },
      lastSync: { startedAtLabel: '25.09.2026 09:12', modeLabel: 'Инкрементная синхронизация' }
    },
    dashboardStatusData: {
      totalCounterparties: total,
      processedCounterparties: local,
      newCounterparties: scenario === 'many' ? 2_314 : 157,
      updatedCounterparties: scenario === 'many' ? 40_870 : 784,
      errorsCount: scenario === 'partial' ? 12 : 4,
      progressPercent: Math.round((local / total) * 100),
      currentStep: 'Загрузка контрагентов из МоегоСклада',
      currentPage: 12,
      totalPages: 18,
      running: true,
      status: 'running'
    }
  }
}

export function buildSettings(scenario) {
  const random = createRandom(3)
  const attributeNames = [
    ['Менеджер', 'employee', false],
    ['Регион', 'customentity', true],
    ['Источник клиента', 'string', false],
    ['Скидка по договору, %', 'double', false],
    ['Дата первой покупки', 'time', false],
    ['Комментарий для склада', 'text', false],
    ['VIP', 'boolean', false],
    ['Код в 1С', 'string', true]
  ]
  const exclusions = [
    { field: 'email', value: 'test@example.com' },
    { field: 'phone', value: '+70000000000' },
    { field: 'name', value: 'Розничный покупатель' }
  ]

  if (scenario === 'long') {
    exclusions.push({ field: 'email', value: LONG_EMAIL }, { field: 'name', value: LONG_COMPANY_NAME })
  }

  return {
    duplicateExclusions: scenario === 'empty' ? [] : exclusions,
    duplicateSearchOptions: { includeArchivedWithDocuments: true },
    fullSyncTimer: { enabled: true, runAt: '03:00' },
    mergeAttributes: scenario === 'empty'
      ? []
      : attributeNames.map(([name, type, required], index) => ({
          id: mockGuid(random),
          name,
          type,
          required,
          enabled: index % 3 !== 2
        })),
    searchLimits: { groupLimit: 200, itemLimit: 100 }
  }
}

export function buildHistory(scenario, query = {}) {
  if (scenario === 'empty') {
    return { rows: [], total: 0 }
  }

  const random = createRandom(9)
  const total = scenario === 'many' ? 1_248 : 37
  const pageSize = Number(query.pageSize || 25)
  const page = Number(query.page || 1)
  const start = (page - 1) * pageSize
  const count = Math.max(0, Math.min(pageSize, total - start))
  const fields = ['name', 'email', 'phone', 'description', 'attribute.region']

  const rows = Array.from({ length: count }, (_, offset) => {
    const index = start + offset
    const name = scenario === 'long' && index % 3 === 0 ? LONG_COMPANY_NAME : pick(random, COMPANY_NAMES)
    const fieldName = fields[index % fields.length]
    const values = {
      name: [`${name} (дубль)`, name],
      email: ['', scenario === 'long' ? LONG_EMAIL : 'info@romashka.ru'],
      phone: ['+7 (912) 000-00-00', '+7 (912) 555-12-34'],
      description: ['', scenario === 'long' ? LONG_DESCRIPTION : 'Оптовый покупатель'],
      'attribute.region': ['{"name":"Москва","meta":{"type":"customentity","href":"https://api.moysklad.ru/x"}}', '{"name":"Санкт-Петербург","meta":{"type":"customentity","href":"https://api.moysklad.ru/y"}}']
    }[fieldName]

    return {
      id: index + 1,
      counterpartyId: mockGuid(random),
      counterpartyName: name,
      counterpartyArchived: index % 5 === 4,
      fieldName,
      oldValue: values[0],
      newValue: values[1],
      changeType: 'merge',
      changedAt: isoMinutesAgo(30 + index * 47)
    }
  })

  return { rows, total }
}

export function buildCounterpartyPage(scenario, id) {
  const random = createRandom(13)
  const name = scenario === 'long' ? LONG_COMPANY_NAME : 'ООО «Ромашка»'
  const item = {
    id: id || mockGuid(random),
    name,
    description: scenario === 'long' ? LONG_DESCRIPTION : 'Основной оптовый покупатель',
    email: scenario === 'long' ? LONG_EMAIL : 'info@romashka.ru',
    phone: '+7 (912) 555-12-34',
    archived: false,
    createdAt: isoMinutesAgo(60 * 24 * 400),
    updatedAt: isoMinutesAgo(60 * 3),
    syncedAt: isoMinutesAgo(40)
  }
  const documentCount = scenario === 'empty' ? 0 : scenario === 'many' ? 60 : 9
  const linkedDocuments = buildMergeDocuments([{ id: item.id, name }], { count: documentCount, seed: 77 }).map(
    (document, index) => ({
      id: index + 1,
      counterpartyId: item.id,
      documentType: document.type,
      documentId: document.id,
      documentHref: '',
      payloadJson: '',
      createdAt: document.moment
    })
  )

  return {
    item,
    rawJson: '{}',
    latestFullExport: scenario === 'empty'
      ? null
      : { id: 1042, payloadJson: '', isPartial: scenario === 'partial', errorCount: scenario === 'partial' ? 3 : 0, createdAt: isoMinutesAgo(90) },
    linkedDocuments,
    linkedDocumentsTotal: scenario === 'many' ? 1_340 : linkedDocuments.length
  }
}
