/*
 * Development-only UI mocks.
 *
 * Enabled with `npm run dev:mock` (VITE_UI_MOCKS=true). main.js imports this
 * module dynamically behind `import.meta.env.DEV`, so it never reaches the
 * production bundle. Views stay unaware of mocks:
 *   - existing endpoints are answered by an axios adapter;
 *   - sections without endpoints receive data through route props,
 *     the same props Histoire stories use.
 * Deleting src/mocks and the guarded block in main.js removes everything.
 */
import { AxiosError } from 'axios'

import { buildDuplicateGroups, fallbackCounterparty, indexCounterparties } from './fixtures/counterparties'
import { buildBusyCounterpartyIds, buildMergeDocuments, buildMergeJobs } from './fixtures/merge'
import { buildCounterpartyPage, buildHistory, buildOverview, buildSettings } from './fixtures/sections'
import { MOCK_SCENARIOS, readMockScenario, writeMockScenario } from './scenarios'

export function installUiMocks({ api, router }) {
  const scenario = readMockScenario()
  const store = createStore(scenario)

  api.defaults.adapter = (config) => handleRequest(config, scenario, store)
  applyRouteProps(router, scenario, store)
  mountScenarioSwitcher(scenario)

  console.info(`[ui-mocks] scenario: ${scenario}`)
}

function createStore(scenario) {
  const groups = buildDuplicateGroups(scenario)
  const jobs = buildMergeJobs(scenario)
  return {
    groups,
    counterparties: indexCounterparties(groups),
    jobs,
    settings: buildSettings(scenario)
  }
}

const ROUTES = [
  ['post', /^\/api\/moysklad\/session$/, () => ok(mockSession())],
  ['get', /^\/api\/moysklad\/session\/me$/, () => ok(mockSession())],
  ['post', /^\/api\/moysklad\/session\/logout$/, () => ok({})],
  ['post', /^\/api\/merge-preview\/selection$/, selectionPreview],
  ['post', /^\/api\/merge-preview$/, duplicatePreview],
  ['post', /^\/api\/merge-jobs$/, createMergeJob],
  ['put', /^\/api\/catalog\/settings$/, saveCatalogSettings],
  ['post', /^\/api\/sync(\/incremental)?$/, startSync]
]

async function handleRequest(config, scenario, store) {
  const method = String(config.method || 'get').toLowerCase()
  const url = String(config.url || '').split('?')[0]
  const route = ROUTES.find(([routeMethod, pattern]) => routeMethod === method && pattern.test(url))
  const isSession = url.startsWith('/api/moysklad/session')

  await delay(isSession ? 250 : 450 + Math.random() * 350)

  if (!isSession && scenario === 'loading') {
    await delay(60_000)
  }

  if (!route) {
    throw axiosError(config, 404, { code: 'MOCK_NOT_FOUND', message: `No mock for ${method.toUpperCase()} ${url}` })
  }

  if (!isSession && scenario === 'error') {
    throw axiosError(config, 503, {
      code: url.includes('merge-preview') ? 'DUPLICATE_PREVIEW_UNAVAILABLE' : 'CATALOG_SYNC_UNAVAILABLE',
      message: 'Upstream service is unavailable.'
    })
  }

  const result = route[2]({ config, store, scenario, body: parseBody(config.data) })
  if (result.status >= 400) {
    throw axiosError(config, result.status, result.data)
  }

  return { data: result.data, status: result.status, statusText: String(result.status), headers: {}, config, request: {} }
}

function duplicatePreview({ config, store }) {
  const fields = readFields(config.params)
  if (!fields.length) {
    return { status: 400, data: { code: 'INVALID_DUPLICATE_FIELDS', message: 'fields must contain name, email, or phone.' } }
  }

  // Groups are filtered by the requested fields the same way the service does.
  return ok(store.groups.filter((group) => fields.includes(group.matchedBy)))
}

function selectionPreview({ body, store }) {
  const ids = Array.isArray(body?.counterpartyIds) ? body.counterpartyIds : []
  const busy = new Set(buildBusyCounterpartyIds(store.jobs))
  if (ids.some((id) => busy.has(id))) {
    return { status: 409, data: { code: 'COUNTERPARTY_BUSY', message: 'Counterparty is busy.' } }
  }

  return ok({
    counterparties: ids.map((id, index) => {
      const item = store.counterparties.get(id) || fallbackCounterparty(id, index)
      return {
        id: item.id,
        name: item.name,
        description: item.description,
        email: item.email,
        phone: item.phone,
        archived: Boolean(item.archived),
        updatedAt: item.updatedAt
      }
    })
  })
}

function createMergeJob({ body, store }) {
  const id = crypto.randomUUID()
  const primary = store.counterparties.get(body?.mainCounterpartyId)
  store.jobs.unshift({
    id,
    status: 'pending',
    primaryCounterpartyId: body?.mainCounterpartyId || '',
    primaryCounterpartyName: body?.mainCounterparty?.name || primary?.name || '',
    secondaryCounterpartyIds: body?.duplicateCounterpartyIds || [],
    secondaryCounterparties: (body?.duplicateCounterpartyIds || []).map((cpId) => ({
      id: cpId,
      name: store.counterparties.get(cpId)?.name || ''
    })),
    createdAt: new Date().toISOString(),
    operations: [],
    documents: []
  })

  return { status: 202, data: { mergeJobId: id, status: 'pending' } }
}

function startSync() {
  return { status: 202, data: { syncRunId: crypto.randomUUID(), status: 'queued' } }
}

function saveCatalogSettings({ store, body }) {
  if (!body) {
    return { status: 400, data: { code: 'INVALID_CATALOG_SETTINGS', message: 'Settings payload is required.' } }
  }

  store.settings = body
  return { status: 204, data: null }
}

/** Feeds sections that have no backend endpoint yet through route props. */
function applyRouteProps(router, scenario, store) {
  const stateProps = scenario === 'loading'
    ? { loading: true }
    : scenario === 'error'
      ? { loadError: 'Сервис временно недоступен' }
      : {}

  // In loading / error scenarios sections get no data, only the state flag.
  const withData = (data) => (scenario === 'loading' || scenario === 'error' ? stateProps : { ...data, ...stateProps })

  const providers = {
    'moysklad-overview': () => withData({
      ...buildOverview(scenario, store.jobs),
      mergeQueueData: { jobs: store.jobs, busyCounterpartyIds: buildBusyCounterpartyIds(store.jobs) }
    }),
    'moysklad-settings': () => ({ settingsData: store.settings }),
    'moysklad-history-counterparties': (route) => withData({ historyData: buildHistory(scenario, route.query) }),
    'moysklad-counterparty': (route) => withData({ counterpartyData: buildCounterpartyPage(scenario, route.params.id) }),
    'moysklad-merge': (route) => {
      const ids = String(route.query.ids || '').split(',').filter(Boolean)
      const participants = ids.map((id, index) => store.counterparties.get(id) || fallbackCounterparty(id, index))
      // `?mockDocs=none` shows the production state where documents are unknown until the job runs.
      if (route.query.mockDocs === 'none') {
        return { documentsPreview: null }
      }
      const count = { empty: 0, many: 240, long: 18, partial: 26 }[scenario] ?? 18
      // Documents belong to every selected counterparty; the view keeps only the duplicates' ones.
      return { documentsPreview: buildMergeDocuments(participants, { count, seed: 8 }) }
    }
  }

  for (const record of router.getRoutes()) {
    const provider = providers[record.name]
    if (provider) {
      record.props = { ...record.props, default: provider }
    }
  }
}

function mountScenarioSwitcher(active) {
  const root = document.createElement('div')
  root.setAttribute('data-ui-mocks', '')
  root.style.cssText = [
    'position:fixed', 'left:12px', 'bottom:12px', 'z-index:3000', 'display:flex', 'align-items:center', 'gap:6px',
    'padding:4px 6px 4px 10px', 'font:12px/1.2 system-ui,sans-serif', 'color:#5d6976', 'background:#fff',
    'border:1px solid #dce1e7', 'border-radius:999px', 'box-shadow:0 4px 14px rgb(20 32 46 / 12%)'
  ].join(';')

  const label = document.createElement('span')
  label.textContent = 'Mock'
  label.style.cssText = 'font-weight:600;color:#1677d2'

  const select = document.createElement('select')
  select.style.cssText = 'font:inherit;border:0;background:transparent;color:#1f2a36;cursor:pointer'
  for (const item of MOCK_SCENARIOS) {
    const option = document.createElement('option')
    option.value = item.value
    option.textContent = item.label
    option.selected = item.value === active
    select.append(option)
  }
  select.addEventListener('change', () => {
    writeMockScenario(select.value)
    const url = new URL(window.location.href)
    url.searchParams.delete('mock')
    window.location.replace(url.toString())
  })

  root.append(label, select)
  document.body.append(root)
}

function readFields(params) {
  if (params instanceof URLSearchParams) {
    return params.getAll('fields')
  }
  const raw = params?.fields
  return Array.isArray(raw) ? raw : raw ? String(raw).split(',') : []
}

function mockSession() {
  return { accountId: '00000000-0000-4000-a000-000000000001', employeeId: '00000000-0000-4000-a000-000000000002', mock: true }
}

function ok(data) {
  return { status: 200, data }
}

function parseBody(data) {
  if (!data || typeof data !== 'string') {
    return data || null
  }
  try {
    return JSON.parse(data)
  } catch {
    return null
  }
}

function axiosError(config, status, data) {
  const response = { data, status, statusText: String(status), headers: { 'x-correlation-id': crypto.randomUUID() }, config }
  return new AxiosError(data.message, AxiosError.ERR_BAD_RESPONSE, config, {}, response)
}

function delay(ms) {
  return new Promise((resolve) => window.setTimeout(resolve, ms))
}
