<script setup>
import { computed, onBeforeUnmount, ref, watch } from 'vue'

import CopyableId from '../ui/CopyableId.vue'
import ErrorNotice from '../ui/ErrorNotice.vue'
import StatusBadge from '../ui/StatusBadge.vue'
import MergeJobDocuments from './MergeJobDocuments.vue'
import MergeJobProgress from './MergeJobProgress.vue'
import { moySkladCounterpartyUrl, normalizeDocumentsInOperation } from '../../domain/documents-in-operation'
import { isJobActive, jobStatusMeta } from '../../domain/merge'
import { COUNTERPARTY_FORMS, countLabel, formatDateTime, shortId } from '../../utils/format'
import { getMergeJobDocumentsInOperation } from '../../api/catalog'
import { toUserError } from '../../utils/errors'

const props = defineProps({
  modelValue: { type: Boolean, default: false },
  job: { type: Object, default: null }
})

const emit = defineEmits(['update:modelValue'])

const visible = computed({
  get: () => props.modelValue,
  set: (value) => emit('update:modelValue', value)
})

const failedOperations = computed(() => (props.job?.operations || []).filter((item) => item.status === 'failed'))
const documentsInOperation = ref([])
const documentsLoading = ref(false)
const documentsError = ref(null)
let documentsRequestVersion = 0
let activeRequestController = null
let documentsRefreshTimer = null

const documentGroups = computed(() => normalizeDocumentsInOperation(documentsInOperation.value))
const failedDocuments = computed(() => {
  const latestDocuments = documentGroups.value.flatMap((group) => group.latestDocuments)
  const failed = latestDocuments.filter((document) => document.result === 'failed')
  return [...new Map(failed.map((document) => [`${document.type}:${document.id}`, document])).values()]
})
const documentCount = computed(() => documentGroups.value.reduce((sum, group) => sum + group.totalCount, 0))
const primaryCounterpartyUrl = computed(() => moySkladCounterpartyUrl(props.job?.primaryCounterpartyId))

const duplicates = computed(() => {
  const job = props.job
  if (!job) {
    return []
  }
  if (job.secondaryCounterparties?.length) {
    return job.secondaryCounterparties
  }
  return job.secondaryCounterpartyIds.map((id) => ({ id, name: '' }))
})

const title = computed(() => props.job?.primaryCounterpartyName || `Объединение ${shortId(props.job?.id)}`)

const documentsReloadKey = computed(() => {
  const job = props.job
  const operations = (job?.operations || [])
    .map((operation) => `${operation.id}:${operation.status}:${operation.attemptCount}:${operation.completedAt}`)
    .join(',')
  return [props.modelValue ? 'open' : 'closed', job?.id || '', job?.updatedAt || '', job?.status || '', operations].join('|')
})

async function loadDocumentsInOperation() {
  const mergeJobId = props.job?.id
  if (!props.modelValue || !mergeJobId) {
    return
  }

  activeRequestController?.abort()
  const controller = new AbortController()
  activeRequestController = controller
  const requestVersion = ++documentsRequestVersion
  documentsLoading.value = true
  documentsError.value = null

  try {
    const response = await getMergeJobDocumentsInOperation(mergeJobId, { signal: controller.signal })
    if (requestVersion !== documentsRequestVersion || props.job?.id !== mergeJobId) {
      return
    }
    documentsInOperation.value = Array.isArray(response.data) ? response.data : []
  } catch (error) {
    if (requestVersion !== documentsRequestVersion || controller.signal.aborted) {
      return
    }
    documentsError.value = toUserError(error, 'Не удалось загрузить результаты документов')
  } finally {
    if (requestVersion === documentsRequestVersion) {
      documentsLoading.value = false
      activeRequestController = null
      scheduleDocumentsRefresh()
    }
  }
}

function clearDocumentsRefreshTimer() {
  if (documentsRefreshTimer) {
    window.clearTimeout(documentsRefreshTimer)
    documentsRefreshTimer = null
  }
}

function scheduleDocumentsRefresh() {
  clearDocumentsRefreshTimer()
  if (!props.modelValue || !isJobActive(props.job?.status)) {
    return
  }

  documentsRefreshTimer = window.setTimeout(() => {
    documentsRefreshTimer = null
    void loadDocumentsInOperation()
  }, 10_000)
}

watch(documentsReloadKey, () => {
  if (props.modelValue && props.job?.id) {
    void loadDocumentsInOperation()
    return
  }

  documentsRequestVersion += 1
  activeRequestController?.abort()
  activeRequestController = null
  clearDocumentsRefreshTimer()
  documentsInOperation.value = []
  documentsError.value = null
  documentsLoading.value = false
}, { immediate: true })

onBeforeUnmount(() => {
  documentsRequestVersion += 1
  activeRequestController?.abort()
  clearDocumentsRefreshTimer()
})
</script>

<template>
  <el-drawer v-model="visible" class="merge-job-drawer" size="min(760px, 100%)" append-to-body>
    <template #header>
      <div v-if="job" class="merge-job-drawer__header">
        <span class="app-meta">Объединение контрагентов</span>
        <h2 class="merge-job-drawer__title">{{ title }}</h2>
        <div class="merge-job-drawer__header-meta">
          <StatusBadge :meta="jobStatusMeta(job.status)" />
          <CopyableId :value="job.id" label="ID задачи" />
        </div>
      </div>
    </template>

    <div v-if="job" class="merge-job-drawer__body">
      <ErrorNotice
        v-if="job.status === 'partial'"
        tone="warning"
        title="Объединение выполнено частично"
        :description="`Операций с ошибкой: ${failedOperations.length}${failedDocuments.length ? `, документов с ошибкой: ${failedDocuments.length}` : ''}. Остальные изменения применены — проверьте отмеченные этапы и документы.`"
      />
      <ErrorNotice
        v-else-if="job.status === 'failed'"
        title="Объединение остановлено"
        :description="job.errorMessage || 'Один из этапов завершился с ошибкой, следующие этапы не выполнялись.'"
      />
      <div v-else-if="job.status === 'pending'" class="merge-job-drawer__note">
        Задача принята и ждёт своей очереди. Выполнение начнётся автоматически — страницу можно закрыть.
      </div>

      <dl class="merge-job-drawer__summary">
        <div>
          <dt>Основной контрагент</dt>
          <dd>
            <a
              v-if="primaryCounterpartyUrl"
              class="merge-job-drawer__link"
              :href="primaryCounterpartyUrl"
              target="_blank"
              rel="noopener noreferrer"
            >
              {{ job.primaryCounterpartyName || shortId(job.primaryCounterpartyId) || '—' }}
            </a>
            <span v-else>{{ job.primaryCounterpartyName || shortId(job.primaryCounterpartyId) || '—' }}</span>
          </dd>
        </div>
        <div>
          <dt>Дубликаты · {{ countLabel(duplicates.length, COUNTERPARTY_FORMS) }}</dt>
          <dd class="merge-job-drawer__duplicates">
            <span v-for="item in duplicates" :key="item.id" class="merge-job-drawer__chip">
              {{ item.name || shortId(item.id) }}
            </span>
            <span v-if="!duplicates.length">—</span>
          </dd>
        </div>
        <div>
          <dt>Создано</dt>
          <dd class="app-nums">{{ formatDateTime(job.createdAt) }}</dd>
        </div>
        <div>
          <dt>Запущено</dt>
          <dd class="app-nums">{{ formatDateTime(job.startedAt) }}</dd>
        </div>
        <div>
          <dt>Завершено</dt>
          <dd class="app-nums">{{ formatDateTime(job.finishedAt) }}</dd>
        </div>
      </dl>

      <section class="merge-job-drawer__section">
        <h3 class="merge-job-drawer__section-title">Ход выполнения</h3>
        <MergeJobProgress :job="job" variant="steps" />
      </section>

      <section class="merge-job-drawer__section">
        <h3 class="merge-job-drawer__section-title">
          Документы
          <span class="app-meta">{{ documentsLoading ? 'Загрузка…' : documentCount || '' }}</span>
        </h3>
        <MergeJobDocuments
          :entries="documentsInOperation"
          :loading="documentsLoading"
          :load-error="documentsError"
          @retry="loadDocumentsInOperation"
        />
      </section>

      <p v-if="job.correlationId" class="merge-job-drawer__tech">
        <CopyableId :value="job.correlationId" label="Correlation ID" full />
      </p>
    </div>
  </el-drawer>
</template>

<style scoped src="../../styles/components/merge/merge-job-drawer.css"></style>
