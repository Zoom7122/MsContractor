<script setup>
import { computed, ref } from 'vue'
import { ArrowDown, ArrowUp } from '@element-plus/icons-vue'

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

function statusType(status) {
  if (status === 'succeeded') return 'success'
  if (status === 'failed' || status === 'interrupted') return 'danger'
  if (status === 'queued' || status === 'running') return 'primary'
  return 'info'
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
  <el-card class="merge-queue-panel" shadow="never">
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

        <el-button
          class="merge-queue-panel__toggle"
          circle
          text
          :aria-expanded="isExpanded"
          :aria-label="isExpanded ? 'Свернуть очередь объединений' : 'Раскрыть очередь объединений'"
          :title="isExpanded ? 'Свернуть' : 'Раскрыть'"
          @click="toggleExpanded"
        >
          <el-icon><component :is="isExpanded ? ArrowUp : ArrowDown" /></el-icon>
        </el-button>
      </div>
    </div>

    <el-skeleton v-if="loading" :rows="2" animated />
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon />
    <el-alert v-if="actionMessage" :title="actionMessage" type="success" :closable="false" show-icon />

    <el-empty v-if="!loading && !mergeJobs.length" description="Очередь пока пуста" :image-size="64" />

    <div v-else class="merge-queue-panel__list">
      <article v-for="job in visibleMergeJobs" :key="job.id" class="merge-queue-panel__item">
        <div class="merge-queue-panel__item-top">
          <div class="merge-queue-panel__item-title">
            <strong>Задача #{{ job.id }}</strong>
            <span>{{ kindLabel(job.kind) }}</span>
          </div>

          <div class="merge-queue-panel__item-actions">
            <el-tag :class="statusClass(job.status)" :type="statusType(job.status)" size="small" effect="light">
              {{ statusLabel(job.status) }}
            </el-tag>
            <el-button
              v-if="canCancel(job)"
              class="merge-queue-panel__cancel"
              type="danger"
              plain
              size="small"
              :loading="cancellingJobId === job.id"
              :disabled="cancellingJobId === job.id"
              @click="handleCancelJob(job.id)"
            >
              Отменить
            </el-button>
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
  </el-card>
</template>

<style scoped src="../../styles/components/merge-queue-panel.css"></style>
