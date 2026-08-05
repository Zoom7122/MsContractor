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

function connectionStatusClass(status) {
  if (status === true) {
    return 'overview-card__status-dot--success'
  }
  if (status === false) {
    return 'overview-card__status-dot--error'
  }
  return 'overview-card__status-dot--neutral'
}

function cardDescription(card) {
  if (card.type === 'connection' && (card.description === null || card.description === undefined || card.description === '')) {
    return 'Ожидает проверки'
  }

  return displayValue(card.description)
}
</script>

<template>
  <section class="overview-top-cards" aria-label="Сводка по МоемуСкладу">
    <article v-for="card in cards" :key="card.key" class="overview-card">
      <h2 class="overview-card__title">{{ card.title }}</h2>

      <div class="overview-card__body">
        <img class="overview-card__icon" :src="card.icon" :alt="card.title" />

        <div class="overview-card__content">
          <div v-if="card.type === 'connection'" class="overview-card__status">
            <span
              class="overview-card__status-dot"
              :class="connectionStatusClass(card.status)"
              aria-hidden="true"
            />
            <span>{{ connectionStatusLabel(card.status) }}</span>
          </div>

          <div v-else class="overview-card__value">
            {{ displayValue(card.value) }}
          </div>

          <p class="overview-card__description">
            {{ cardDescription(card) }}
          </p>
        </div>
      </div>
    </article>
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

  <section class="sync-panel">
    <div class="sync-panel__main">
      <h2 class="sync-panel__title">Синхронизация с МойСклад</h2>

      <div class="sync-panel__progress-row">
        <div class="sync-panel__progress">
          <div
            class="sync-panel__progress-fill"
            :style="{ width: `${syncProgressPercent}%` }"
          />
        </div>

        <span class="sync-panel__percent">
          {{ syncProgressPercent }}%
        </span>
      </div>

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
      <button
        type="button"
        class="sync-panel__btn sync-panel__btn--primary"
        :disabled="syncActionLoading"
        @click="handleFullSync"
      >
        {{ syncActionLoading ? 'Запуск…' : 'Полная синхронизация' }}
      </button>

      <button
        type="button"
        class="sync-panel__btn sync-panel__btn--outline"
        :disabled="syncActionLoading"
        @click="handleIncrementalSync"
      >
        {{ syncActionLoading ? 'Запуск…' : 'Инкрементная синхронизация' }}
      </button>

      <button
        type="button"
        class="sync-panel__btn sync-panel__btn--danger"
        disabled
      >
        Отменить синхронизацию
      </button>

      <p v-if="syncActionMessage" class="sync-panel__message" role="status">
        {{ syncActionMessage }}
      </p>
      <p v-if="syncActionError" class="sync-panel__error" role="alert">
        {{ syncActionError }}
      </p>
    </div>
  </section>
</template>

<style>
.overview-top-cards {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
  width: 100%;
}

.overview-card {
  min-height: 128px;
  padding: 22px;
  background: #ffffff;
  border: 1px solid #dfe7f3;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(15, 35, 80, 0.06);
}

.overview-card__title {
  margin: 0 0 18px;
  color: #50648f;
  font-size: 16px;
  font-weight: 600;
  line-height: 1.25;
}

.overview-card__body {
  display: flex;
  align-items: center;
  gap: 16px;
}

.overview-card__icon {
  flex: 0 0 auto;
  width: 58px;
  height: 58px;
  object-fit: contain;
}

.overview-card__content {
  min-width: 0;
}

.overview-card__value {
  color: #0f1b3d;
  font-size: 24px;
  font-weight: 700;
  line-height: 1.2;
}

.overview-card__description {
  margin: 6px 0 0;
  color: #64759b;
  font-size: 14px;
  line-height: 1.35;
}

.overview-card__status {
  display: flex;
  align-items: center;
  gap: 8px;
  color: #0f1b3d;
  font-size: 18px;
  font-weight: 700;
  line-height: 1.2;
}

.overview-card__status-dot {
  flex: 0 0 auto;
  width: 10px;
  height: 10px;
  border-radius: 50%;
}

.overview-card__status-dot--success {
  background: #20b26b;
}

.overview-card__status-dot--error {
  background: #e5484d;
}

.overview-card__status-dot--neutral {
  background: #aab4c5;
}

.overview-queue-section {
  margin-top: 20px;
}

.overview-queue-section__intro {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 18px;
  margin-bottom: 14px;
}

.overview-queue-section__kicker {
  display: inline-flex;
  margin-bottom: 6px;
  color: #335c99;
  font-size: 12px;
  font-weight: 800;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.overview-queue-section__intro h2 {
  margin: 0;
  color: #0f1b3d;
  font-size: 24px;
  font-weight: 800;
  line-height: 1.2;
}

.overview-queue-section__intro p {
  max-width: 520px;
  margin: 0;
  color: #64759b;
  font-size: 14px;
  line-height: 1.5;
  text-align: right;
}

.sync-panel {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 244px;
  gap: 24px;
  width: 100%;
  margin-top: 20px;
  padding: 24px;
  background: #ffffff;
  border: 1px solid #dfe7f3;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(15, 35, 80, 0.06);
}

.sync-panel__main {
  min-width: 0;
}

.sync-panel__title {
  margin: 0 0 18px;
  color: #0f1b3d;
  font-size: 22px;
  font-weight: 700;
  line-height: 1.25;
}

.sync-panel__loading {
  margin: 0 0 12px;
  color: #64759b;
  font-size: 14px;
}

.sync-panel__progress-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: center;
  gap: 14px;
}

.sync-panel__progress {
  height: 12px;
  overflow: hidden;
  background: #edf2f8;
  border-radius: 999px;
}

.sync-panel__progress-fill {
  height: 100%;
  background: #2d6cdf;
  border-radius: inherit;
  transition: width 0.2s ease;
}

.sync-panel__percent {
  min-width: 44px;
  color: #0f1b3d;
  font-size: 16px;
  font-weight: 700;
  text-align: right;
}

.sync-panel__stats {
  display: flex;
  flex-wrap: wrap;
  gap: 0;
  margin-top: 20px;
}

.sync-panel__stat {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 132px;
  padding: 0 18px;
  border-right: 1px solid #dfe7f3;
}

.sync-panel__stat:first-child {
  padding-left: 0;
}

.sync-panel__stat:last-child {
  border-right: 0;
}

.sync-panel__stat-label {
  color: #64759b;
  font-size: 13px;
  line-height: 1.25;
}

.sync-panel__stat strong {
  color: #0f1b3d;
  font-size: 20px;
  font-weight: 700;
  line-height: 1.2;
}

.sync-panel__stat-blue {
  color: #2d6cdf !important;
}

.sync-panel__stat-red {
  color: #d92d3f !important;
}

.sync-panel__footer {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 20px;
  color: #64759b;
  font-size: 14px;
  line-height: 1.4;
}

.sync-panel__footer strong {
  color: #0f1b3d;
  font-weight: 700;
}

.sync-panel__actions {
  display: flex;
  flex-direction: column;
  gap: 12px;
  align-self: start;
}

.sync-panel__btn {
  width: 100%;
  min-height: 42px;
  padding: 10px 16px;
  border: 1px solid transparent;
  border-radius: 8px;
  font-size: 14px;
  font-weight: 700;
  line-height: 1.2;
  cursor: pointer;
  transition:
    background 0.2s ease,
    border-color 0.2s ease,
    color 0.2s ease,
    opacity 0.2s ease;
}

.sync-panel__btn:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.sync-panel__btn--primary {
  color: #ffffff;
  background: #2d6cdf;
  border-color: #2d6cdf;
}

.sync-panel__btn--outline {
  color: #2d6cdf;
  background: #ffffff;
  border-color: #9bb8ef;
}

.sync-panel__btn--danger {
  color: #d92d3f;
  background: #fff5f6;
  border-color: #f2a4ad;
}

.sync-panel__error,
.sync-panel__message {
  margin: 14px 0 0;
  font-size: 14px;
  line-height: 1.4;
}

.sync-panel__error {
  color: #d92d3f;
}

.sync-panel__message {
  color: #1f8f5f;
}

@media (max-width: 1200px) {
  .overview-top-cards {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }

  .sync-panel {
    grid-template-columns: 1fr;
  }

  .overview-queue-section__intro {
    flex-direction: column;
    align-items: flex-start;
  }

  .overview-queue-section__intro p {
    max-width: none;
    text-align: left;
  }

  .sync-panel__actions {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 800px) {
  .overview-top-cards {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .sync-panel__stats {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 14px;
  }

  .sync-panel__stat {
    min-width: 0;
    padding: 0;
    border-right: 0;
  }

  .sync-panel__actions {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 520px) {
  .overview-top-cards {
    grid-template-columns: 1fr;
  }

  .sync-panel {
    padding: 20px;
  }

  .sync-panel__stats {
    grid-template-columns: 1fr;
  }

  .sync-panel__progress-row {
    grid-template-columns: 1fr;
  }

  .sync-panel__percent {
    text-align: left;
  }
}
</style>
