<script setup>
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ArrowRight } from '@element-plus/icons-vue'

import EmptyState from '../ui/EmptyState.vue'
import ErrorNotice from '../ui/ErrorNotice.vue'
import SectionPanel from '../ui/SectionPanel.vue'
import StatusBadge from '../ui/StatusBadge.vue'
import MergeJobDrawer from '../merge/MergeJobDrawer.vue'
import MergeJobProgress from '../merge/MergeJobProgress.vue'
import { isJobActive, isJobFinished, jobStatusMeta, normalizeMergeJob } from '../../domain/merge'
import { DUPLICATE_FORMS, countLabel, formatDateTimeShort, formatNumber, shortId } from '../../utils/format'

/**
 * Merge jobs queue. Merge commands are answered with 202 Accepted, so this
 * panel is where the user follows the actual execution.
 * `data` — `{ jobs, busyCounterpartyIds }`; null while the status source is not connected.
 */
const props = defineProps({
  data: { type: Object, default: null },
  loading: { type: Boolean, default: false },
  loadError: { type: [String, Object], default: null },
  collapsedLimit: { type: Number, default: 5 }
})

const router = useRouter()
const isExpanded = ref(false)
const drawerOpen = ref(false)
const selectedJobId = ref('')

const mergeJobs = computed(() => (Array.isArray(props.data?.jobs) ? props.data.jobs.map(normalizeMergeJob) : []))
const busyCounterpartyIds = computed(() => (Array.isArray(props.data?.busyCounterpartyIds) ? props.data.busyCounterpartyIds : []))
const unavailable = computed(() => !props.data && !props.loading && !props.loadError)

const activeJobsCount = computed(() => mergeJobs.value.filter((job) => isJobActive(job.status)).length)
const completedJobsCount = computed(() => mergeJobs.value.filter((job) => isJobFinished(job.status)).length)
const problemJobsCount = computed(() =>
  mergeJobs.value.filter((job) => job.status === 'partial' || job.status === 'failed').length
)

const visibleMergeJobs = computed(() =>
  isExpanded.value ? mergeJobs.value : mergeJobs.value.slice(0, props.collapsedLimit)
)
const hiddenCount = computed(() => Math.max(0, mergeJobs.value.length - props.collapsedLimit))
const selectedJob = computed(() => mergeJobs.value.find((job) => job.id === selectedJobId.value) || null)

function openJob(job) {
  selectedJobId.value = job.id
  drawerOpen.value = true
}

function jobTitle(job) {
  return job.primaryCounterpartyName || `Объединение ${shortId(job.id)}`
}

function jobSubtitle(job) {
  const duplicates = job.secondaryCounterpartyIds.length
  const parts = [duplicates ? `+ ${countLabel(duplicates, DUPLICATE_FORMS)}` : 'без дубликатов']
  parts.push(formatDateTimeShort(job.finishedAt || job.startedAt || job.createdAt))
  return parts.join(' · ')
}
</script>

<template>
  <SectionPanel
    class="merge-queue"
    title="Очередь объединений"
    subtitle="Задачи выполняются в фоне — здесь видно, на каком они этапе"
    flush
  >
    <template #actions>
      <dl v-if="!unavailable && !loading && !loadError" class="merge-queue__counters">
        <div>
          <dt>Активные</dt>
          <dd class="app-nums">{{ formatNumber(activeJobsCount) }}</dd>
        </div>
        <div>
          <dt>Завершены</dt>
          <dd class="app-nums">{{ formatNumber(completedJobsCount) }}</dd>
        </div>
        <div :class="{ 'merge-queue__counter--warning': problemJobsCount }">
          <dt>С ошибками</dt>
          <dd class="app-nums">{{ formatNumber(problemJobsCount) }}</dd>
        </div>
        <div>
          <el-tooltip content="Контрагенты из активных задач нельзя объединять повторно, пока задача не завершится" placement="top">
            <dt class="merge-queue__hint-term">Заблокировано КА</dt>
          </el-tooltip>
          <dd class="app-nums">{{ formatNumber(busyCounterpartyIds.length) }}</dd>
        </div>
      </dl>
    </template>

    <div v-if="loading" class="merge-queue__skeleton" aria-busy="true">
      <el-skeleton v-for="index in 3" :key="index" animated>
        <template #template>
          <div class="merge-queue__skeleton-row">
            <el-skeleton-item variant="button" style="width: 90px; height: 22px" />
            <el-skeleton-item variant="text" style="width: 38%" />
            <el-skeleton-item variant="text" style="width: 22%" />
          </div>
        </template>
      </el-skeleton>
    </div>

    <div v-else-if="loadError" class="merge-queue__state">
      <ErrorNotice :error="loadError" fallback="Не удалось загрузить очередь объединений" />
    </div>

    <EmptyState
      v-else-if="unavailable"
      size="sm"
      image="unavailable"
      title="Статусы задач пока недоступны"
      description="Объединения принимаются и выполняются как обычно. Ход выполнения появится здесь, когда подключится сервис статусов."
    />

    <EmptyState
      v-else-if="!mergeJobs.length"
      size="sm"
      image="queue"
      title="Очередь пуста"
      description="Выберите группу дублей и отправьте её на объединение — задача появится здесь."
    >
      <el-button size="small" @click="router.push({ name: 'moysklad-duplicates' })">Найти дубли</el-button>
    </EmptyState>

    <ul v-else class="merge-queue__list">
      <li v-for="job in visibleMergeJobs" :key="job.id">
        <button
          type="button"
          class="merge-queue__row"
          :class="`merge-queue__row--${job.status}`"
          @click="openJob(job)"
        >
          <span class="merge-queue__status">
            <StatusBadge size="sm" :meta="jobStatusMeta(job.status)" />
          </span>
          <span class="merge-queue__main">
            <span class="merge-queue__title app-truncate">{{ jobTitle(job) }}</span>
            <span class="merge-queue__subtitle app-truncate">{{ jobSubtitle(job) }}</span>
          </span>
          <span class="merge-queue__progress">
            <MergeJobProgress :job="job" />
          </span>
          <el-icon class="merge-queue__chevron" aria-hidden="true"><ArrowRight /></el-icon>
        </button>
      </li>
    </ul>

    <MergeJobDrawer v-model="drawerOpen" :job="selectedJob" />

    <template v-if="hiddenCount > 0" #footer>
      <span class="app-meta">Показано {{ visibleMergeJobs.length }} из {{ mergeJobs.length }}</span>
      <el-button text type="primary" size="small" :aria-expanded="isExpanded" @click="isExpanded = !isExpanded">
        {{ isExpanded ? 'Свернуть' : `Показать все (${mergeJobs.length})` }}
      </el-button>
    </template>
  </SectionPanel>
</template>

<style scoped src="../../styles/components/merge-queue-panel.css"></style>
