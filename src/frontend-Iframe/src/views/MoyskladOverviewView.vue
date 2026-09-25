<script setup>
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import {
  Connection,
  CopyDocument,
  Refresh,
  Timer,
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
import { startFullSync, startIncrementalSync } from '../api/sync'
import { readAccepted, toUserError } from '../utils/errors'
import { formatNumber } from '../utils/format'

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
})

const router = useRouter()
const overviewData = ref(createEmptyOverviewData())
const dashboardStatus = ref(createEmptyDashboardStatus())
const syncActionMode = ref('')
const syncAccepted = ref(null)
const syncActionError = ref(null)

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

const hasOverviewData = computed(() => Boolean(props.overviewData))
const hasStatusData = computed(() => Boolean(props.dashboardStatusData) || Boolean(syncAccepted.value))
const syncActionLoading = computed(() => Boolean(syncActionMode.value))
const syncProgressPercent = computed(() => calculateSyncProgress(overviewData.value))
const normalizedDashboardStatus = computed(() => dashboardStatus.value)

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
  const total = data.connection.counterpartyTotal

  return [
    {
      key: 'counterparties',
      label: 'Контрагентов в базе',
      icon: User,
      value: known ? formatNumber(data.local.counterpartiesCount) : '—',
      hint: !known ? 'Нет данных' : total > 0 ? `из ${formatNumber(total)} в МоёмСкладе` : 'Синхронизация не выполнялась',
    },
    {
      key: 'duplicates',
      label: 'Групп дублей',
      icon: CopyDocument,
      tone: 'warning',
      value: known ? formatNumber(data.duplicates.groupsCount) : '—',
      hint: known ? 'по последнему поиску' : 'Нет данных',
      to: { name: 'moysklad-duplicates' },
      linkLabel: 'К дублям',
    },
    {
      key: 'mergeQueue',
      label: 'Объединений в работе',
      icon: Timer,
      tone: 'neutral',
      value: known ? formatNumber(data.mergeQueue.jobsCount) : '—',
      hint: known ? 'в очереди и выполняются' : 'Нет данных',
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
  if (status.running && ['queued', 'pending', 'accepted'].includes(String(status.status))) {
    return { label: 'В очереди', tone: 'neutral' }
  }
  if (status.running) {
    return { label: 'Выполняется', tone: 'primary', spinning: true }
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
  const value = (number) => (known ? formatNumber(number) : '—')
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
    processedCounterparties: 0,
    newCounterparties: 0,
    updatedCounterparties: 0,
    errorsCount: 0,
    progressPercent: 0,
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
  const processedCounterparties = toNumber(
    source?.processedCounterparties ?? status.processed ?? lastRun.totalCounterparties,
    empty.processedCounterparties
  )
  const updatedCounterparties = toNumber(
    source?.updatedCounterparties ?? status.upserted ?? lastRun.upsertedCounterparties,
    empty.updatedCounterparties
  )
  const errorsCount = toNumber(
    source?.errorsCount ?? source?.lastRunErrorsCount ?? (Array.isArray(source?.lastRunErrors) ? source.lastRunErrors.length : undefined),
    empty.errorsCount
  )
  const progressSource = source?.progressPercent ?? status.progressPercent

  return {
    totalCounterparties,
    processedCounterparties,
    newCounterparties: toNumber(source?.newCounterparties, empty.newCounterparties),
    updatedCounterparties,
    errorsCount,
    progressPercent: normalizeProgressPercent(progressSource, processedCounterparties, totalCounterparties),
    currentStep: source?.currentStep ?? status.currentStep ?? null,
    currentPage: normalizeOptionalNumber(source?.currentPage ?? status.currentPage),
    totalPages: normalizeOptionalNumber(source?.totalPages ?? status.totalPages),
    running: Boolean(source?.running ?? status.running),
    status: source?.status && typeof source.status !== 'object' ? source.status : status.status ?? null,
  }
}

function calculateSyncProgress(source) {
  const total = Number(source?.connection?.counterpartyTotal || 0)
  const localCount = Number(source?.local?.counterpartiesCount || 0)

  if (total <= 0 || localCount <= 0) {
    return 0
  }

  const percent = Math.round((localCount / total) * 100)

  return Math.min(100, Math.max(0, percent))
}

function normalizeOptionalNumber(value) {
  const numberValue = Number(value)
  return Number.isFinite(numberValue) && numberValue > 0 ? numberValue : null
}

function normalizeProgressPercent(progressPercent, processedCounterparties, totalCounterparties) {
  let percent = Number(progressPercent)
  if (!Number.isFinite(percent)) {
    percent = totalCounterparties > 0 ? (processedCounterparties / totalCounterparties) * 100 : 0
  }

  return Math.round(Math.min(100, Math.max(0, percent)))
}

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

    <ErrorNotice v-if="loadError" :error="loadError" fallback="Не удалось загрузить сводку" />

    <section class="overview-kpis" aria-label="Сводка">
      <div class="overview-kpis__connection">
        <div class="overview-kpis__connection-head">
          <span class="overview-kpis__connection-icon" aria-hidden="true">
            <el-icon><Connection /></el-icon>
          </span>
          <span class="overview-kpis__label">МойСклад</span>
        </div>
        <el-skeleton v-if="loading" animated :rows="1" />
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
        :loading="loading"
      />
    </section>

    <div class="overview-grid">
      <MergeQueuePanel
        class="overview-grid__queue"
        :data="mergeQueueData"
        :loading="loading"
        :load-error="loadError ? 'Очередь объединений недоступна' : null"
      />

      <SectionPanel class="sync-panel" title="Синхронизация" subtitle="Загрузка контрагентов из МоегоСклада">
        <template #actions>
          <StatusBadge
            v-if="!loading"
            :label="syncState.label"
            :tone="syncState.tone"
            :spinning="syncState.spinning"
            :icon="syncState.spinning ? Refresh : null"
          />
        </template>

        <el-skeleton v-if="loading" :rows="5" animated />

        <div v-else class="sync-panel__body">
          <p v-if="needsFirstSync" class="sync-panel__hint">
            Данных в базе ещё нет. Запустите полную синхронизацию — после неё можно искать и объединять дубли.
          </p>

          <div class="sync-panel__progress">
            <div class="sync-panel__progress-head">
              <span class="sync-panel__progress-label">Загружено в базу</span>
              <span class="sync-panel__progress-value app-nums">{{ syncProgressPercent }}%</span>
            </div>
            <el-progress
              :percentage="syncProgressPercent"
              :stroke-width="8"
              :show-text="false"
              :status="syncProgressPercent === 100 ? 'success' : undefined"
            />
            <span class="sync-panel__progress-caption app-nums">
              <template v-if="hasOverviewData && overviewData.connection.counterpartyTotal > 0">
                {{ formatNumber(overviewData.local.counterpartiesCount) }} из {{ formatNumber(overviewData.connection.counterpartyTotal) }} контрагентов
              </template>
              <template v-else>Нет данных о количестве контрагентов</template>
            </span>
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
              :disabled="syncActionLoading || loading"
              @click="handleFullSync"
            >
              Полная синхронизация
            </el-button>
            <el-button
              :loading="syncActionMode === 'incremental'"
              :disabled="syncActionLoading || loading"
              @click="handleIncrementalSync"
            >
              Инкрементная
            </el-button>
          </div>
          <el-tooltip v-if="normalizedDashboardStatus.running && !loading" content="Остановка синхронизации пока недоступна" placement="top">
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
