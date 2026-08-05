<script setup>
import { computed, ref } from 'vue'

const mergeJobs = ref([])
const busyCounterpartyIds = ref([])
const loading = ref(false)
const error = ref('Раздел временно недоступен')
const actionMessage = ref(null)
const actionError = ref(null)
const cancellingJobId = ref(0)
const isExpanded = ref(false)

const activeJobsCount = computed(() =>
  mergeJobs.value.filter((job) => job.status === 'queued' || job.status === 'running').length
)

const completedJobsCount = computed(() =>
  mergeJobs.value.filter((job) => ['succeeded', 'failed', 'cancelled', 'interrupted'].includes(job.status)).length
)

const visibleMergeJobs = computed(() => {
  if (isExpanded.value) {
    return mergeJobs.value
  }

  return mergeJobs.value.slice(0, 1)
})

function handleCancelJob() {
  actionMessage.value = null
  actionError.value = 'Раздел временно недоступен'
}

function toggleExpanded() {
  isExpanded.value = !isExpanded.value
}

function normalizeMergeJob(item) {
  return {
    id: Number(item?.id || 0),
    kind: String(item?.kind || ''),
    status: String(item?.status || ''),
    primaryCounterpartyId: String(item?.primaryCounterpartyId || ''),
    secondaryCounterpartyIds: Array.isArray(item?.secondaryCounterpartyIds)
      ? item.secondaryCounterpartyIds.map((value) => String(value || '').trim()).filter(Boolean)
      : [],
    errorMessage: String(item?.errorMessage || ''),
    createdAt: String(item?.createdAt || ''),
    startedAt: String(item?.startedAt || ''),
    finishedAt: String(item?.finishedAt || '')
  }
}

function statusLabel(status) {
  switch (status) {
    case 'queued':
      return 'В очереди'
    case 'running':
      return 'В работе'
    case 'succeeded':
      return 'Завершено'
    case 'failed':
      return 'Ошибка'
    case 'cancelled':
      return 'Отменено'
    case 'interrupted':
      return 'Прервано'
    default:
      return 'Неизвестно'
  }
}

function statusClass(status) {
  return {
    'merge-queue-panel__status': true,
    'merge-queue-panel__status--queued': status === 'queued',
    'merge-queue-panel__status--running': status === 'running',
    'merge-queue-panel__status--succeeded': status === 'succeeded',
    'merge-queue-panel__status--failed': status === 'failed',
    'merge-queue-panel__status--cancelled': status === 'cancelled',
    'merge-queue-panel__status--interrupted': status === 'interrupted'
  }
}

function kindLabel(kind) {
  return kind === 'batch' ? 'Пакетное' : 'Обычное'
}

function displayValue(value, fallback = '—') {
  return value ? value : fallback
}

function secondaryCounterpartiesLabel(job) {
  const count = job.secondaryCounterpartyIds.length
  if (!count) {
    return 'Без дублей'
  }

  return `${count} ${pluralizeCounterparties(count)}`
}

function pluralizeCounterparties(count) {
  const mod10 = count % 10
  const mod100 = count % 100

  if (mod10 === 1 && mod100 !== 11) {
    return 'дубликат'
  }
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) {
    return 'дубликата'
  }
  return 'дубликатов'
}

function canCancel(job) {
  return false
}
</script>

<template>
  <section class="merge-queue-panel">
    <div class="merge-queue-panel__header">
      <div>
        <h2>Живая очередь объединений</h2>
        <p>Активные и последние задачи объединения контрагентов</p>
      </div>

      <div class="merge-queue-panel__controls">
        <div class="merge-queue-panel__summary">
          <div class="merge-queue-panel__summary-card">
            <span>Активных</span>
            <strong>{{ activeJobsCount }}</strong>
          </div>
          <div class="merge-queue-panel__summary-card">
            <span>Завершённых</span>
            <strong>{{ completedJobsCount }}</strong>
          </div>
          <div class="merge-queue-panel__summary-card">
            <span>Заблокировано КА</span>
            <strong>{{ busyCounterpartyIds.length }}</strong>
          </div>
        </div>

        <button
          class="merge-queue-panel__toggle"
          type="button"
          :aria-expanded="isExpanded"
          :aria-label="isExpanded ? 'Свернуть очередь объединений' : 'Раскрыть очередь объединений'"
          :title="isExpanded ? 'Свернуть' : 'Раскрыть'"
          @click="toggleExpanded"
        >
          <span class="merge-queue-panel__toggle-chevron" />
        </button>
      </div>
    </div>

    <p v-if="loading" class="merge-queue-panel__message">Загружаем очередь объединений...</p>
    <p v-if="error" class="merge-queue-panel__error">{{ error }}</p>
    <p v-if="actionError" class="merge-queue-panel__error">{{ actionError }}</p>
    <p v-if="actionMessage" class="merge-queue-panel__success">{{ actionMessage }}</p>

    <div v-if="!loading && !mergeJobs.length" class="merge-queue-panel__empty">
      Очередь пока пуста.
    </div>

    <div v-else class="merge-queue-panel__list">
      <article v-for="job in visibleMergeJobs" :key="job.id" class="merge-queue-panel__item">
        <div class="merge-queue-panel__item-top">
          <div class="merge-queue-panel__item-title">
            <strong>Задача #{{ job.id }}</strong>
            <span>{{ kindLabel(job.kind) }}</span>
          </div>

          <div class="merge-queue-panel__item-actions">
            <span :class="statusClass(job.status)">
              {{ statusLabel(job.status) }}
            </span>
            <button
              v-if="canCancel(job)"
              class="merge-queue-panel__cancel"
              type="button"
              :disabled="cancellingJobId === job.id"
              @click="handleCancelJob(job.id)"
            >
              {{ cancellingJobId === job.id ? 'Отмена...' : 'Отменить' }}
            </button>
          </div>
        </div>

        <dl class="merge-queue-panel__meta">
          <div>
            <dt>Главный КА</dt>
            <dd>{{ displayValue(job.primaryCounterpartyId) }}</dd>
          </div>
          <div>
            <dt>Дубликаты</dt>
            <dd>{{ secondaryCounterpartiesLabel(job) }}</dd>
          </div>
          <div>
            <dt>Создано</dt>
            <dd>{{ displayValue(job.createdAt) }}</dd>
          </div>
          <div>
            <dt>Старт</dt>
            <dd>{{ displayValue(job.startedAt) }}</dd>
          </div>
          <div>
            <dt>Завершено</dt>
            <dd>{{ displayValue(job.finishedAt) }}</dd>
          </div>
        </dl>

        <p v-if="job.errorMessage" class="merge-queue-panel__job-error">
          {{ job.errorMessage }}
        </p>
      </article>
    </div>
  </section>
</template>

<style scoped>
.merge-queue-panel {
  padding: 24px;
  background: #ffffff;
  border: 1px solid #dfe7f3;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(15, 35, 80, 0.06);
}

.merge-queue-panel__header {
  display: flex;
  justify-content: space-between;
  gap: 20px;
  margin-bottom: 18px;
}

.merge-queue-panel__header h2 {
  margin: 0;
  color: #0f1b3d;
  font-size: 22px;
  font-weight: 700;
}

.merge-queue-panel__header p {
  margin: 6px 0 0;
  color: #64759b;
  font-size: 14px;
}

.merge-queue-panel__summary {
  display: grid;
  grid-template-columns: repeat(3, minmax(120px, 1fr));
  gap: 12px;
  min-width: 340px;
}

.merge-queue-panel__controls {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.merge-queue-panel__toggle {
  position: relative;
  width: 40px;
  height: 40px;
  flex: 0 0 auto;
  color: #1d4ed8;
  background: #f8fbff;
  border: 1px solid #d8e4f5;
  border-radius: 8px;
  cursor: pointer;
}

.merge-queue-panel__toggle:hover {
  background: #edf4ff;
}

.merge-queue-panel__toggle-chevron {
  position: absolute;
  top: 13px;
  left: 13px;
  width: 12px;
  height: 12px;
  border-right: 2px solid currentColor;
  border-bottom: 2px solid currentColor;
  transform: rotate(45deg);
  transition: transform 0.18s ease;
}

.merge-queue-panel__toggle[aria-expanded="true"] .merge-queue-panel__toggle-chevron {
  transform: translateY(4px) rotate(225deg);
}

.merge-queue-panel__summary-card {
  display: grid;
  gap: 4px;
  padding: 14px;
  background: #f8fbff;
  border: 1px solid #d8e4f5;
  border-radius: 12px;
}

.merge-queue-panel__summary-card span {
  color: #64759b;
  font-size: 12px;
  font-weight: 700;
}

.merge-queue-panel__summary-card strong {
  color: #0f1b3d;
  font-size: 22px;
  font-weight: 800;
}

.merge-queue-panel__message,
.merge-queue-panel__error,
.merge-queue-panel__success {
  margin: 0 0 12px;
  font-size: 14px;
  line-height: 1.4;
}

.merge-queue-panel__error {
  color: #d92d3f;
}

.merge-queue-panel__success {
  color: #157347;
}

.merge-queue-panel__empty {
  padding: 18px;
  color: #64759b;
  font-size: 14px;
  background: #f8fafd;
  border: 1px dashed #d8e4f5;
  border-radius: 12px;
}

.merge-queue-panel__list {
  display: grid;
  gap: 14px;
}

.merge-queue-panel__item {
  padding: 18px;
  background: linear-gradient(180deg, #ffffff, #f9fbff);
  border: 1px solid #dfe7f3;
  border-radius: 14px;
}

.merge-queue-panel__item-top {
  display: flex;
  justify-content: space-between;
  gap: 16px;
}

.merge-queue-panel__item-title {
  display: grid;
  gap: 4px;
}

.merge-queue-panel__item-title strong {
  color: #0f1b3d;
  font-size: 16px;
  font-weight: 800;
}

.merge-queue-panel__item-title span {
  color: #64759b;
  font-size: 13px;
}

.merge-queue-panel__item-actions {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}

.merge-queue-panel__status {
  display: inline-flex;
  min-height: 28px;
  align-items: center;
  padding: 0 12px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 800;
}

.merge-queue-panel__status--queued {
  color: #9b5d00;
  background: #fff1d6;
}

.merge-queue-panel__status--running {
  color: #1d4ed8;
  background: #e8f0ff;
}

.merge-queue-panel__status--succeeded {
  color: #157347;
  background: #e5f7ee;
}

.merge-queue-panel__status--failed,
.merge-queue-panel__status--interrupted {
  color: #b42318;
  background: #fee4e2;
}

.merge-queue-panel__status--cancelled {
  color: #50648f;
  background: #edf2fb;
}

.merge-queue-panel__cancel {
  min-height: 28px;
  padding: 0 12px;
  color: #d92d3f;
  background: #fff5f6;
  border: 1px solid #f2a4ad;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 700;
  cursor: pointer;
}

.merge-queue-panel__cancel:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.merge-queue-panel__meta {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
  margin: 16px 0 0;
}

.merge-queue-panel__meta div {
  display: grid;
  gap: 4px;
}

.merge-queue-panel__meta dt {
  color: #64759b;
  font-size: 12px;
  font-weight: 700;
}

.merge-queue-panel__meta dd {
  margin: 0;
  color: #0f1b3d;
  font-size: 13px;
  line-height: 1.4;
  word-break: break-word;
}

.merge-queue-panel__job-error {
  margin: 14px 0 0;
  font-size: 13px;
  line-height: 1.45;
}

.merge-queue-panel__job-error {
  color: #b42318;
}

@media (max-width: 980px) {
  .merge-queue-panel__header {
    flex-direction: column;
  }

  .merge-queue-panel__summary {
    min-width: 0;
  }

  .merge-queue-panel__controls {
    align-items: stretch;
  }

  .merge-queue-panel__meta {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 640px) {
  .merge-queue-panel {
    padding: 20px;
  }

  .merge-queue-panel__summary {
    grid-template-columns: 1fr;
  }

  .merge-queue-panel__controls {
    flex-direction: column;
  }

  .merge-queue-panel__toggle {
    align-self: flex-end;
  }

  .merge-queue-panel__item-top,
  .merge-queue-panel__item-actions {
    flex-direction: column;
    align-items: flex-start;
  }

  .merge-queue-panel__meta {
    grid-template-columns: 1fr;
  }
}
</style>
