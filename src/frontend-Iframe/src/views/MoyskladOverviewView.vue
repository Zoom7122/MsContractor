<script setup>
import { computed, ref, watch } from 'vue'

import MergeQueuePanel from '../components/overview/MergeQueuePanel.vue'
import { startFullSync, startIncrementalSync } from '../api/sync'
import MSlogo from '../assets/MSlogo.png'
import iconCounterpartiesCard from '../assets/icon_counterparties_card.png'
import iconMergeQueueCard from '../assets/icon_merge_queue_card.png'
import iconSyncCard from '../assets/icon_sync_card.png'

const props = defineProps({
  overviewData: {
    type: Object,
    default: null,
  },
  dashboardStatusData: {
    type: Object,
    default: null,
  },
})

const overviewData = ref(createEmptyOverviewData())
const dashboardStatus = ref(createEmptyDashboardStatus())
const overviewLoading = ref(false)
const statusLoading = ref(false)
const statusError = ref('Раздел временно недоступен')
const syncActionLoading = ref(false)
const syncActionMessage = ref(null)
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

const cards = computed(() => [
  {
    key: 'connection',
    title: 'Подключение к МС',
    icon: MSlogo,
    type: 'connection',
    status: overviewData.value.connection.ok,
    value: overviewData.value.connection.label,
    description: overviewData.value.connection.description,
  },
  {
    key: 'counterparties',
    title: 'Контрагентов в базе',
    icon: iconCounterpartiesCard,
    value: overviewData.value.local.counterpartiesCount,
    description: 'Всего в базе',
  },
  {
    key: 'mergeQueue',
    title: 'Очередь объединений КА',
    icon: iconMergeQueueCard,
    value: overviewData.value.mergeQueue.jobsCount,
    description: 'В обработке',
  },
  {
    key: 'lastSync',
    title: 'Последняя синхронизация',
    icon: iconSyncCard,
    value: overviewData.value.lastSync.startedAtLabel,
    description: overviewData.value.lastSync.modeLabel,
  },
])

const normalizedDashboardStatus = computed(() => dashboardStatus.value)
const syncProgressPercent = computed(() => calculateSyncProgress(overviewData.value))

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

async function handleFullSync() {
  syncActionMessage.value = null
  syncActionError.value = null
  syncActionLoading.value = true

  try {
    const result = await startFullSync()
    syncActionMessage.value = 'Полная синхронизация поставлена в очередь'
    dashboardStatus.value = {
      ...dashboardStatus.value,
      running: true,
      status: result?.status || 'queued',
      currentStep: 'Ожидание запуска синхронизации',
    }
  } catch (error) {
    if (error?.status === 401) {
      syncActionError.value = 'Сессия истекла. Откройте приложение заново через МойСклад.'
    } else if (error?.status === 503) {
      syncActionError.value = 'Очередь синхронизации временно недоступна. Повторите попытку позже.'
    } else {
      syncActionError.value = error?.message || 'Не удалось запустить полную синхронизацию'
    }
  } finally {
    syncActionLoading.value = false
  }
}

function markSyncUnavailable() {
  syncActionMessage.value = null
  syncActionError.value = 'Раздел временно недоступен'
}

async function handleIncrementalSync() {
  syncActionMessage.value = null
  syncActionError.value = null
  syncActionLoading.value = true

  try {
    const result = await startIncrementalSync()
    syncActionMessage.value = 'Инкрементная синхронизация поставлена в очередь'
    dashboardStatus.value = {
      ...dashboardStatus.value,
      running: true,
      status: result?.status || 'queued',
      currentStep: 'Ожидание запуска синхронизации',
    }
  } catch (error) {
    if (error?.status === 401) {
      syncActionError.value = 'Сессия истекла. Откройте приложение заново через МойСклад.'
    } else if (error?.status === 503) {
      syncActionError.value = 'Очередь синхронизации временно недоступна. Повторите попытку позже.'
    } else {
      syncActionError.value = error?.message || 'Не удалось запустить инкрементную синхронизацию'
    }
  } finally {
    syncActionLoading.value = false
  }
}

const handleCancelSync = markSyncUnavailable

function displayValue(value) {
  if (value === null || value === undefined || value === '') {
    return '—'
  }

  return value
}

function connectionStatusLabel(status) {
  if (status === true) {
    return 'Подключено'
  }
  if (status === false) {
    return 'Не подключено'
  }
  return 'Нет данных'
}

function connectionTagType(status) {
  if (status === true) {
    return 'success'
  }
  if (status === false) {
    return 'danger'
  }
  return 'info'
}

function cardDescription(card) {
  if (card.type === 'connection' && (card.description === null || card.description === undefined || card.description === '')) {
    return 'Ожидает проверки'
  }

  return displayValue(card.description)
}
</script>

<template>
  <section v-loading="overviewLoading || statusLoading" class="overview-top-cards" aria-label="Сводка по МоемуСкладу">
    <el-card v-for="card in cards" :key="card.key" class="overview-card" shadow="never">
      <h2 class="overview-card__title">{{ card.title }}</h2>

      <div class="overview-card__body">
        <img class="overview-card__icon" :src="card.icon" :alt="card.title" />

        <div class="overview-card__content">
          <div v-if="card.type === 'connection'" class="overview-card__status">
            <el-tag :type="connectionTagType(card.status)" size="small" effect="light">
              {{ connectionStatusLabel(card.status) }}
            </el-tag>
          </div>

          <div v-else class="overview-card__value">
            {{ displayValue(card.value) }}
          </div>

          <p class="overview-card__description">
            {{ cardDescription(card) }}
          </p>
        </div>
      </div>
    </el-card>
  </section>

  <section class="overview-queue-section">
    <div class="overview-queue-section__intro">
      <div>
        <span class="overview-queue-section__kicker">Контрагенты</span>
        <h2>Очередь объединений по КА</h2>
      </div>
      <p>Активные merge-задачи, последние результаты и заблокированные контрагенты в одном месте.</p>
    </div>

    <MergeQueuePanel />
  </section>

  <el-card class="sync-panel" shadow="never">
    <div class="sync-panel__main">
      <h2 class="sync-panel__title">Синхронизация с МойСклад</h2>

      <el-progress
        class="sync-panel__progress-row"
        :percentage="syncProgressPercent"
        :stroke-width="10"
      />

      <div class="sync-panel__stats">
        <div class="sync-panel__stat">
          <span class="sync-panel__stat-label">Всего контрагентов в базе</span>
          <strong>{{ overviewData.local.counterpartiesCount }}</strong>
        </div>

        <div class="sync-panel__stat">
          <span class="sync-panel__stat-label">Обработано</span>
          <strong>{{ normalizedDashboardStatus.processedCounterparties }}</strong>
        </div>

        <div class="sync-panel__stat">
          <span class="sync-panel__stat-label">Новых</span>
          <strong class="sync-panel__stat-blue">{{ normalizedDashboardStatus.newCounterparties }}</strong>
        </div>

        <div class="sync-panel__stat">
          <span class="sync-panel__stat-label">Обновлено</span>
          <strong class="sync-panel__stat-blue">{{ normalizedDashboardStatus.updatedCounterparties }}</strong>
        </div>

        <div class="sync-panel__stat">
          <span class="sync-panel__stat-label">Ошибки</span>
          <strong class="sync-panel__stat-red">{{ normalizedDashboardStatus.errorsCount }}</strong>
        </div>
      </div>

      <div class="sync-panel__footer">
        <span>Текущий шаг:</span>
        <strong>{{ normalizedDashboardStatus.currentStep || 'Нет активной синхронизации' }}</strong>

        <span v-if="normalizedDashboardStatus.currentPage && normalizedDashboardStatus.totalPages">
          (страница {{ normalizedDashboardStatus.currentPage }} из {{ normalizedDashboardStatus.totalPages }})
        </span>
      </div>
    </div>

    <div class="sync-panel__actions">
      <el-button
        type="primary"
        class="sync-panel__btn sync-panel__btn--primary"
        :loading="syncActionLoading"
        :disabled="syncActionLoading"
        @click="handleFullSync"
      >
        Полная синхронизация
      </el-button>

      <el-button
        plain
        class="sync-panel__btn sync-panel__btn--outline"
        :loading="syncActionLoading"
        :disabled="syncActionLoading"
        @click="handleIncrementalSync"
      >
        Инкрементная синхронизация
      </el-button>

      <el-button
        type="danger"
        plain
        class="sync-panel__btn sync-panel__btn--danger"
        disabled
      >
        Отменить синхронизацию
      </el-button>

      <el-alert v-if="syncActionMessage" :title="syncActionMessage" type="success" :closable="false" show-icon />
      <el-alert v-if="syncActionError" :title="syncActionError" type="error" :closable="false" show-icon />
    </div>
  </el-card>
</template>

<style scoped src="../styles/pages/overview.css"></style>
