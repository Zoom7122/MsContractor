<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import {
  Connection,
  CopyDocument,
  Refresh,
  User,
  VideoPause
} from '@element-plus/icons-vue'

import MergeQueuePanel from '../components/overview/MergeQueuePanel.vue'
import AcceptedNotice from '../components/ui/AcceptedNotice.vue'
import ErrorNotice from '../components/ui/ErrorNotice.vue'
import PageHeader from '../components/ui/PageHeader.vue'
import SectionPanel from '../components/ui/SectionPanel.vue'
import StatTile from '../components/ui/StatTile.vue'
import StatusBadge from '../components/ui/StatusBadge.vue'
import { getCatalogState } from '../api/catalog'
import { startFullSync, startIncrementalSync } from '../api/sync'
import { readAccepted, toUserError } from '../utils/errors'
import { formatDateTime, formatNumber } from '../utils/format'

const props = defineProps({
  overviewData: {
    type: Object,
    default: null,
  },
  dashboardStatusData: {
    type: Object,
    default: null,
  },
  mergeQueueData: {
    type: Object,
    default: null,
  },
  loading: {
    type: Boolean,
    default: false,
  },
  loadError: {
    type: [String, Object],
    default: null,
  },
  autoLoad: {
    type: Boolean,
    default: true,
  },
})

const router = useRouter()
const overviewData = ref(createEmptyOverviewData())
const dashboardStatus = ref(createEmptyDashboardStatus())
const syncActionMode = ref('')
const syncAccepted = ref(null)
const syncActionError = ref(null)
const catalogState = ref(null)
const queueData = ref(props.mergeQueueData)
const stateLoading = ref(false)
const stateLoadError = ref(null)
let stateRefreshTimer = null

watch(
  () => props.overviewData,
  (value) => {
    if (value) {
      overviewData.value = normalizeOverviewData(value)
    }
  },
  { immediate: true }
)

watch(
  () => props.dashboardStatusData,
  (value) => {
    if (value) {
      dashboardStatus.value = normalizeDashboardStatus(value)
    }
  },
  { immediate: true }
)

watch(
  () => props.mergeQueueData,
  (value) => {
    if (value) {
      queueData.value = value
    }
  },
  { immediate: true }
)

const hasOverviewData = computed(() => Boolean(props.overviewData) || Boolean(catalogState.value))
const hasStatusData = computed(() => Boolean(props.dashboardStatusData) || Boolean(catalogState.value) || Boolean(syncAccepted.value))
const syncActionLoading = computed(() => Boolean(syncActionMode.value))
const isLoading = computed(() => props.loading || stateLoading.value)
const currentLoadError = computed(() => props.loadError || stateLoadError.value)
const syncProgressPercent = computed(() => normalizedDashboardStatus.value.progressPercent)
const normalizedDashboardStatus = computed(() => dashboardStatus.value)
const syncProgressAvailable = computed(() => normalizedDashboardStatus.value.progressAvailable)
const syncProgressCaption = computed(() => {
  const status = normalizedDashboardStatus.value
  if (status.status === 'running' || status.status === 'queued') {
    return 'Статистика обработки появится после завершения запуска'
  }
  if (status.status === 'failed') {
    return 'Итоговая статистика недоступна: запуск завершился с ошибкой'
  }
  if (status.status === 'completed') {
    return `${formatNumber(status.processedCounterparties)} из ${formatNumber(status.totalCounterparties)} обработано за запуск`
  }
  return 'Синхронизация ещё не выполнялась'
})

const connectionBadge = computed(() => {
  const status = overviewData.value.connection.ok
  if (!hasOverviewData.value || status === null || status === undefined) {
    return { label: 'Нет данных', tone: 'neutral' }
  }
  return status ? { label: 'Подключено', tone: 'success' } : { label: 'Не подключено', tone: 'danger' }
})

const connectionHint = computed(() => {
  const connection = overviewData.value.connection
  if (!hasOverviewData.value) {
    return 'Ожидает проверки'
  }
  if (connection.description) {
    return connection.description
  }
  return connection.counterpartyTotal > 0 ? `Контрагентов в МС: ${formatNumber(connection.counterpartyTotal)}` : 'Ожидает проверки'
})

const tiles = computed(() => {
  const data = overviewData.value
  const known = hasOverviewData.value

  return [
    {
      key: 'counterparties',
      label: 'Контрагентов в базе',
      icon: User,
      value: known ? formatNumber(data.local.counterpartiesCount) : '—',
      hint: known ? 'В каталоге' : 'Нет данных',
    },
    {
      key: 'lastSync',
      label: 'Последняя синхронизация',
      icon: Refresh,
      tone: 'success',
      value: displayValue(data.lastSync.startedAtLabel),
      hint: data.lastSync.modeLabel || 'Ещё не запускалась',
    },
  ]
})

const syncState = computed(() => {
  const status = normalizedDashboardStatus.value
  const normalizedStatus = String(status.status || '').toLowerCase()
  if (status.running && ['queued', 'pending', 'accepted'].includes(normalizedStatus)) {
    return { label: 'В очереди', tone: 'neutral' }
  }
  if (status.running) {
    return { label: 'Выполняется', tone: 'primary', spinning: true }
  }
  if (normalizedStatus === 'completed') {
    return { label: 'Завершена', tone: 'success' }
  }
  if (normalizedStatus === 'failed') {
    return { label: 'Ошибка', tone: 'danger' }
  }
  if (!hasStatusData.value) {
    return { label: 'Нет данных', tone: 'neutral' }
  }
  return { label: 'Не запущена', tone: 'neutral' }
})

const needsFirstSync = computed(() =>
  hasOverviewData.value &&
  overviewData.value.local.counterpartiesCount === 0 &&
  !normalizedDashboardStatus.value.running
)

const syncStats = computed(() => {
  const status = normalizedDashboardStatus.value
  const known = hasStatusData.value
  const value = (number) => (known && number !== null && number !== undefined ? formatNumber(number) : '—')
  return [
    { key: 'processed', label: 'Обработано', value: value(status.processedCounterparties) },
    { key: 'new', label: 'Новых', value: value(status.newCounterparties) },
    { key: 'updated', label: 'Обновлено', value: value(status.updatedCounterparties) },
    { key: 'errors', label: 'Ошибок', value: value(status.errorsCount), danger: known && status.errorsCount > 0 },
  ]
})

function createEmptyOverviewData() {
  return {
    connection: {
      ok: null,
      label: null,
      description: null,
      counterpartyTotal: 0,
    },
    local: {
      counterpartiesCount: 0,
    },
    duplicates: {
      groupsCount: 0,
    },
    mergeQueue: {
      jobsCount: 0,
    },
    lastSync: {
      startedAtLabel: null,
      modeLabel: null,
    },
  }
}

function createEmptyDashboardStatus() {
  return {
    totalCounterparties: 0,
    processedCounterparties: null,
    newCounterparties: null,
    updatedCounterparties: null,
    errorsCount: null,
    progressPercent: 0,
    progressAvailable: false,
    currentStep: null,
    currentPage: null,
    totalPages: null,
    running: false,
    status: null,
  }
}

function normalizeOverviewData(source) {
  const empty = createEmptyOverviewData()
  const connection = source?.connection || {}
  const local = source?.local || {}
  const duplicates = source?.duplicates || {}
  const mergeQueue = source?.mergeQueue || {}
  const lastSync = source?.lastSync || {}
  const counterpartyTotal = toNumber(connection.counterpartyTotal, empty.connection.counterpartyTotal)

  return {
    connection: {
      ok: connection.ok ?? empty.connection.ok,
      label: connection.label ?? empty.connection.label,
      description: connection.description ?? (counterpartyTotal > 0 ? `Контрагентов в МС: ${counterpartyTotal}` : empty.connection.description),
      counterpartyTotal,
    },
    local: {
      counterpartiesCount: toNumber(local.counterpartiesCount, empty.local.counterpartiesCount),
    },
    duplicates: {
      groupsCount: toNumber(duplicates.groupsCount, empty.duplicates.groupsCount),
    },
    mergeQueue: {
      jobsCount: toNumber(mergeQueue.jobsCount, empty.mergeQueue.jobsCount),
    },
    lastSync: {
      startedAtLabel: lastSync.startedAtLabel ?? empty.lastSync.startedAtLabel,
      modeLabel: lastSync.modeLabel ?? empty.lastSync.modeLabel,
    },
  }
}

function toNumber(value, fallback) {
  const numberValue = Number(value)
  return Number.isFinite(numberValue) ? numberValue : fallback
}

function normalizeDashboardStatus(source) {
  const empty = createEmptyDashboardStatus()
  const status = source?.status && typeof source.status === 'object' ? source.status : source || {}
  const lastRun = source?.lastRun || source?.lastSync || {}
  const totalCounterparties = toNumber(
    source?.totalCounterparties ?? source?.counterpartiesCount ?? lastRun.totalCounterparties,
    empty.totalCounterparties
  )
  const processedCounterparties = toOptionalNumber(
    source?.processedCounterparties ?? status.processed ?? lastRun.totalCounterparties
  )
  const updatedCounterparties = toOptionalNumber(
    source?.updatedCounterparties ?? status.upserted ?? lastRun.upsertedCounterparties
  )
  const errorsCount = toOptionalNumber(
    source?.errorsCount ?? source?.lastRunErrorsCount ?? (Array.isArray(source?.lastRunErrors) ? source.lastRunErrors.length : undefined),
  )
  const progressSource = source?.progressPercent ?? status.progressPercent

  return {
    totalCounterparties,
    processedCounterparties,
    newCounterparties: toOptionalNumber(source?.newCounterparties),
    updatedCounterparties,
    errorsCount,
    progressPercent: normalizeProgressPercent(progressSource, processedCounterparties, totalCounterparties),
    progressAvailable: Boolean(source?.progressAvailable ?? (progressSource !== null && progressSource !== undefined)),
    currentStep: source?.currentStep ?? status.currentStep ?? null,
    currentPage: normalizeOptionalNumber(source?.currentPage ?? status.currentPage),
    totalPages: normalizeOptionalNumber(source?.totalPages ?? status.totalPages),
    running: Boolean(source?.running ?? status.running),
    status: source?.status && typeof source.status !== 'object' ? source.status : status.status ?? null,
  }
}

function normalizeOptionalNumber(value) {
  const numberValue = Number(value)
  return Number.isFinite(numberValue) && numberValue > 0 ? numberValue : null
}

function normalizeProgressPercent(progressPercent, processedCounterparties, totalCounterparties) {
  let percent = progressPercent === null || progressPercent === undefined || progressPercent === ''
    ? Number.NaN
    : Number(progressPercent)
  if (!Number.isFinite(percent)) {
    percent = totalCounterparties > 0 && processedCounterparties !== null
      ? (processedCounterparties / totalCounterparties) * 100
      : 0
  }

  return Math.round(Math.min(100, Math.max(0, percent)))
}

function toOptionalNumber(value) {
  if (value === null || value === undefined || value === '') {
    return null
  }

  const numberValue = Number(value)
  return Number.isFinite(numberValue) ? numberValue : null
}

function mapCatalogState(state) {
  const run = state?.latestSyncRun || null
  const acceptedRunPending = Boolean(syncAccepted.value) && String(run?.id || '').toLowerCase() !== String(syncAccepted.value.id || '').toLowerCase()
  const connected = state?.moySklad?.connected
  const mode = String(run?.executionMode || '').toLowerCase()
  const totalCount = toNumber(run?.totalCount, 0)
  const startedAt = run?.startedAt || run?.createdAt || null
  const connectionDescription = state?.moySklad?.errorMessage || (
    connected
      ? 'Подключение к МоемуСкладу доступно'
      : 'Проверьте подключение к МоемуСкладу'
  )
  const jobs = Array.isArray(state?.latestMergeJobs) ? state.latestMergeJobs : []
  const mappedJobs = jobs.map((job) => {
    const operations = Array.isArray(job?.operations) ? job.operations : []
    const mainCounterparty = readJobPayload(job?.payload)
    const duplicateCounterpartyIds = operations
      .filter((operation) => String(operation?.type || '').toLowerCase() === 'archive_duplicate')
      .map((operation) => String(operation?.counterpartyId || ''))
      .filter(Boolean)

    return {
      id: job?.id,
      status: job?.status,
      primaryCounterpartyId: job?.mainCounterpartyId,
      primaryCounterpartyName: mainCounterparty?.name || '',
      secondaryCounterpartyIds: duplicateCounterpartyIds,
      correlationId: job?.correlationId,
      createdAt: job?.createdAt,
      startedAt: job?.startedAt,
      finishedAt: job?.completedAt,
      operations: operations.map((operation) => ({
        id: operation?.id,
        type: operation?.type,
        status: operation?.status,
        counterpartyId: operation?.counterpartyId,
        attemptCount: operation?.attemptCount,
        errorCode: operation?.errorCode,
        errorMessage: operation?.errorMessage,
        startedAt: operation?.startedAt,
        completedAt: operation?.completedAt
      })),
      documents: []
    }
  })
  const activeStatuses = ['pending', 'queued', 'accepted', 'running']
  const busyCounterpartyIds = mappedJobs
    .filter((job) => activeStatuses.includes(String(job.status || '').toLowerCase()))
    .flatMap((job) => [job.primaryCounterpartyId, ...job.secondaryCounterpartyIds])
    .filter(Boolean)
  const isSyncRunning = acceptedRunPending || activeStatuses.includes(String(run?.status || '').toLowerCase())

  return {
    overviewData: {
      connection: {
        ok: typeof connected === 'boolean' ? connected : null,
        label: connected ? 'Подключено' : 'Не подключено',
        description: connectionDescription,
        counterpartyTotal: 0
      },
      local: { counterpartiesCount: toNumber(state?.counterpartyCount, 0) },
      duplicates: { groupsCount: 0 },
      mergeQueue: { jobsCount: mappedJobs.filter((job) => activeStatuses.includes(String(job.status || '').toLowerCase())).length },
      lastSync: {
        startedAtLabel: startedAt ? formatDateTime(startedAt) : null,
        modeLabel: run ? `${mode === 'incremental' ? 'Инкрементная' : 'Полная'} синхронизация` : 'Ещё не запускалась'
      }
    },
    dashboardStatusData: {
      totalCounterparties: totalCount,
      processedCounterparties: run?.status === 'completed' ? toOptionalNumber(run.processedCount) : null,
      newCounterparties: null,
      updatedCounterparties: null,
      errorsCount: null,
      progressPercent: run?.status === 'completed' ? normalizeProgressPercent(null, toOptionalNumber(run.processedCount), totalCount) : 0,
      progressAvailable: run?.status === 'completed' && totalCount > 0,
      currentStep: acceptedRunPending || run?.status === 'queued'
        ? 'Ожидание запуска синхронизации'
        : run?.status === 'running' ? 'Синхронизация контрагентов' : null,
      currentPage: null,
      totalPages: null,
      running: isSyncRunning,
      status: acceptedRunPending ? 'queued' : run?.status || 'idle'
    },
    mergeQueueData: { jobs: mappedJobs, busyCounterpartyIds }
  }
}

function readJobPayload(payload) {
  if (!payload || typeof payload !== 'string') {
    return {}
  }

  try {
    return JSON.parse(payload)
  } catch {
    return {}
  }
}

function hasInjectedData() {
  return Boolean(props.overviewData || props.dashboardStatusData || props.mergeQueueData)
}

async function loadCatalogState() {
  stateLoading.value = true
  stateLoadError.value = null
  try {
    const state = await getCatalogState()
    catalogState.value = state
    const mapped = mapCatalogState(state)
    overviewData.value = normalizeOverviewData(mapped.overviewData)
    dashboardStatus.value = normalizeDashboardStatus(mapped.dashboardStatusData)
    queueData.value = mapped.mergeQueueData
  } catch (error) {
    stateLoadError.value = toUserError(error, 'Не удалось загрузить сводку')
  } finally {
    stateLoading.value = false
    scheduleStateRefresh()
  }
}

function scheduleStateRefresh() {
  if (stateRefreshTimer) {
    window.clearTimeout(stateRefreshTimer)
    stateRefreshTimer = null
  }

  const syncIsRunning = normalizedDashboardStatus.value.running
  const mergeIsRunning = queueData.value?.jobs?.some((job) => ['pending', 'queued', 'accepted', 'running'].includes(String(job?.status || '').toLowerCase()))
  if (!syncIsRunning && !mergeIsRunning) {
    return
  }

  stateRefreshTimer = window.setTimeout(() => {
    void loadCatalogState()
  }, 10_000)
}

onMounted(() => {
  if (props.autoLoad && !hasInjectedData()) {
    void loadCatalogState()
  }
})

onBeforeUnmount(() => {
  if (stateRefreshTimer) {
    window.clearTimeout(stateRefreshTimer)
  }
})

async function runSync(mode) {
  syncAccepted.value = null
  syncActionError.value = null
  syncActionMode.value = mode

  const isFull = mode === 'full'
  try {
    const response = await (isFull ? startFullSync() : startIncrementalSync())
    const accepted = readAccepted(response)
    syncAccepted.value = {
      id: accepted.id,
      title: isFull ? 'Полная синхронизация поставлена в очередь' : 'Инкрементная синхронизация поставлена в очередь',
    }
    dashboardStatus.value = {
      ...dashboardStatus.value,
      running: true,
      status: accepted.status || 'queued',
      currentStep: 'Ожидание запуска синхронизации',
    }
    void loadCatalogState()
  } catch (error) {
    syncActionError.value = toUserError(
      error,
      isFull ? 'Не удалось запустить полную синхронизацию' : 'Не удалось запустить инкрементную синхронизацию'
    )
  } finally {
    syncActionMode.value = ''
  }
}

function handleFullSync() {
  return runSync('full')
}

function handleIncrementalSync() {
  return runSync('incremental')
}

function displayValue(value) {
  if (value === null || value === undefined || value === '') {
    return '—'
  }

  return value
}
</script>

<template>
  <div class="app-page overview-page">
    <PageHeader
      title="Обзор"
      subtitle="Подключение, синхронизация и ход объединений контрагентов"
    >
      <template #actions>
        <el-button :icon="CopyDocument" @click="router.push({ name: 'moysklad-duplicates' })">
          Найти дубли
        </el-button>
      </template>
    </PageHeader>

    <ErrorNotice v-if="currentLoadError" :error="currentLoadError" fallback="Не удалось загрузить сводку" />

    <section class="overview-kpis" aria-label="Сводка">
      <div class="overview-kpis__connection">
        <div class="overview-kpis__connection-head">
          <span class="overview-kpis__connection-icon" aria-hidden="true">
            <el-icon><Connection /></el-icon>
          </span>
          <span class="overview-kpis__label">МойСклад</span>
        </div>
        <el-skeleton v-if="isLoading" animated :rows="1" />
        <template v-else>
          <StatusBadge :label="connectionBadge.label" :tone="connectionBadge.tone" />
          <span class="overview-kpis__hint">{{ connectionHint }}</span>
        </template>
      </div>

      <StatTile
        v-for="tile in tiles"
        :key="tile.key"
        class="overview-kpis__tile"
        :label="tile.label"
        :value="tile.value"
        :hint="tile.hint"
        :icon="tile.icon"
        :tone="tile.tone"
        :to="tile.to"
        :link-label="tile.linkLabel"
        :loading="isLoading"
      />
    </section>

    <div class="overview-grid">
      <MergeQueuePanel
        class="overview-grid__queue"
        :data="queueData"
        :loading="isLoading"
        :load-error="currentLoadError ? 'Очередь объединений недоступна' : null"
      />

      <SectionPanel class="sync-panel" title="Синхронизация" subtitle="Загрузка контрагентов из МоегоСклада">
        <template #actions>
          <StatusBadge
            v-if="!isLoading"
            :label="syncState.label"
            :tone="syncState.tone"
            :spinning="syncState.spinning"
            :icon="syncState.spinning ? Refresh : null"
          />
        </template>

        <el-skeleton v-if="isLoading" :rows="5" animated />

        <div v-else class="sync-panel__body">
          <p v-if="needsFirstSync" class="sync-panel__hint">
            Данных в базе ещё нет. Запустите полную синхронизацию — после неё можно искать и объединять дубли.
          </p>

          <div class="sync-panel__progress">
            <div class="sync-panel__progress-head">
              <span class="sync-panel__progress-label">Обработка последнего запуска</span>
              <span v-if="syncProgressAvailable" class="sync-panel__progress-value app-nums">{{ syncProgressPercent }}%</span>
            </div>
            <el-progress
              v-if="syncProgressAvailable"
              :percentage="syncProgressPercent"
              :stroke-width="8"
              :show-text="false"
              :status="syncProgressPercent === 100 ? 'success' : undefined"
            />
            <span class="sync-panel__progress-caption app-nums">{{ syncProgressCaption }}</span>
          </div>

          <dl class="sync-panel__stats">
            <div v-for="stat in syncStats" :key="stat.key" class="sync-panel__stat">
              <dt>{{ stat.label }}</dt>
              <dd class="app-nums" :class="{ 'sync-panel__stat-value--danger': stat.danger }">{{ stat.value }}</dd>
            </div>
          </dl>

          <p class="sync-panel__step">
            <span class="app-text-muted">Текущий шаг:</span>
            {{ normalizedDashboardStatus.currentStep || 'Нет активной синхронизации' }}
            <span
              v-if="normalizedDashboardStatus.currentPage && normalizedDashboardStatus.totalPages"
              class="app-text-muted app-nums"
            >
              · страница {{ normalizedDashboardStatus.currentPage }} из {{ normalizedDashboardStatus.totalPages }}
            </span>
          </p>

          <AcceptedNotice
            v-if="syncAccepted"
            :title="syncAccepted.title"
            description="Запрос принят. Синхронизация выполняется в фоне — прогресс обновится после её запуска."
            :operation-id="syncAccepted.id"
            operation-label="ID запуска"
            closable
            @close="syncAccepted = null"
          />
          <ErrorNotice v-if="syncActionError" :error="syncActionError" />
        </div>

        <template #footer>
          <div class="sync-panel__actions">
            <el-button
              type="primary"
              :loading="syncActionMode === 'full'"
              :disabled="syncActionLoading || isLoading"
              @click="handleFullSync"
            >
              Полная синхронизация
            </el-button>
            <el-button
              :loading="syncActionMode === 'incremental'"
              :disabled="syncActionLoading || isLoading"
              @click="handleIncrementalSync"
            >
              Инкрементная
            </el-button>
          </div>
          <el-tooltip v-if="normalizedDashboardStatus.running && !isLoading" content="Остановка синхронизации пока недоступна" placement="top">
            <span>
              <el-button text type="danger" :icon="VideoPause" disabled>Остановить</el-button>
            </span>
          </el-tooltip>
        </template>
      </SectionPanel>
    </div>
  </div>
</template>

<style scoped src="../styles/pages/overview.css"></style>
