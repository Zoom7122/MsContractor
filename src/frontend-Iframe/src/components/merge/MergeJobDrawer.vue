<script setup>
import { computed } from 'vue'
import { useRouter } from 'vue-router'

import CopyableId from '../ui/CopyableId.vue'
import EmptyState from '../ui/EmptyState.vue'
import ErrorNotice from '../ui/ErrorNotice.vue'
import StatusBadge from '../ui/StatusBadge.vue'
import MergeDocumentsTable from './MergeDocumentsTable.vue'
import MergeJobProgress from './MergeJobProgress.vue'
import { jobStatusMeta } from '../../domain/merge'
import { COUNTERPARTY_FORMS, countLabel, formatDateTime, shortId } from '../../utils/format'

const props = defineProps({
  modelValue: { type: Boolean, default: false },
  job: { type: Object, default: null }
})

const emit = defineEmits(['update:modelValue'])
const router = useRouter()

const visible = computed({
  get: () => props.modelValue,
  set: (value) => emit('update:modelValue', value)
})

const failedOperations = computed(() => (props.job?.operations || []).filter((item) => item.status === 'failed'))
const failedDocuments = computed(() => (props.job?.documents || []).filter((item) => item.result === 'failed'))

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

function openCounterparty(id) {
  if (!id) {
    return
  }
  visible.value = false
  router.push({ name: 'moysklad-counterparty', params: { id } })
}
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
            <button type="button" class="merge-job-drawer__link" @click="openCounterparty(job.primaryCounterpartyId)">
              {{ job.primaryCounterpartyName || shortId(job.primaryCounterpartyId) || '—' }}
            </button>
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
          <span class="app-meta">{{ job.documents.length || '' }}</span>
        </h3>
        <MergeDocumentsTable v-if="job.documents.length" :documents="job.documents" mode="result" :page-size="15" />
        <EmptyState
          v-else
          size="sm"
          image="documents"
          :title="job.status === 'pending' ? 'Документы ещё не найдены' : 'Документы не затронуты'"
          :description="
            job.status === 'pending'
              ? 'Список появится после этапа подготовки.'
              : 'У дубликатов не нашлось документов для переноса, или сервис не вернул их список.'
          "
        />
      </section>

      <p v-if="job.correlationId" class="merge-job-drawer__tech">
        <CopyableId :value="job.correlationId" label="Correlation ID" full />
      </p>
    </div>
  </el-drawer>
</template>

<style scoped src="../../styles/components/merge/merge-job-drawer.css"></style>
