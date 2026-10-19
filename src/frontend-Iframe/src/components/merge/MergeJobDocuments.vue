<script setup>
import { computed, ref, watch } from 'vue'

import EmptyState from '../ui/EmptyState.vue'
import ErrorNotice from '../ui/ErrorNotice.vue'
import MergeDocumentsTable from './MergeDocumentsTable.vue'
import StatusBadge from '../ui/StatusBadge.vue'
import { normalizeDocumentsInOperation } from '../../domain/documents-in-operation'
import { formatNumber } from '../../utils/format'

const props = defineProps({
  entries: { type: Array, default: () => [] },
  loading: { type: Boolean, default: false },
  loadError: { type: [String, Object, Error], default: null }
})

const emit = defineEmits(['retry'])
const expandedGroups = ref([])
const groups = computed(() => normalizeDocumentsInOperation(props.entries))
const documentCount = computed(() => groups.value.reduce((sum, group) => sum + group.totalCount, 0))

watch(() => props.entries, () => {
  expandedGroups.value = []
})

function responseTone(statusCode) {
  if (statusCode === null) return 'neutral'
  if (statusCode >= 500 || statusCode < 200) return 'danger'
  if (statusCode >= 400 || statusCode >= 300) return 'warning'
  return 'success'
}

function responseLabel(statusCode) {
  return statusCode === null ? 'Ответ' : `HTTP ${statusCode}`
}
</script>

<template>
  <div class="merge-job-documents">
    <el-skeleton v-if="loading" :rows="4" animated aria-busy="true" />

    <ErrorNotice
      v-else-if="loadError"
      :error="loadError"
      fallback="Не удалось загрузить результаты документов"
      retryable
      retry-label="Повторить"
      @retry="emit('retry')"
    />

    <EmptyState
      v-else-if="!groups.length"
      size="sm"
      image="documents"
      title="Сохранённых результатов по документам пока нет"
      description="Когда этапы объединения вернут результаты, они появятся здесь."
    />

    <template v-else>
      <p class="merge-job-documents__count app-meta">
        Результатов по документам: {{ formatNumber(documentCount) }}
      </p>

      <el-collapse v-model="expandedGroups" class="merge-job-documents__groups">
        <el-collapse-item v-for="group in groups" :key="group.operationType" :name="group.operationType">
          <template #title>
            <div class="merge-job-documents__group-title">
              <span>{{ group.label }}</span>
              <span class="merge-job-documents__group-count app-meta">
                {{ formatNumber(group.totalCount) }}
                <template v-if="group.skippedCount"> · пропущено {{ formatNumber(group.skippedCount) }}</template>
                <template v-if="group.failedCount"> · ошибок {{ formatNumber(group.failedCount) }}</template>
              </span>
            </div>
          </template>

          <div class="merge-job-documents__attempts">
            <section v-for="attempt in group.attempts" :key="attempt.id" class="merge-job-documents__attempt">
              <div class="merge-job-documents__response">
                <StatusBadge size="sm" :tone="responseTone(attempt.statusCode)" :label="responseLabel(attempt.statusCode)" />
                <span v-if="attempt.errorCode" class="app-meta app-mono">{{ attempt.errorCode }}</span>
                <span v-if="attempt.message" class="merge-job-documents__message">{{ attempt.message }}</span>
              </div>

              <MergeDocumentsTable
                v-if="attempt.documents.length"
                :documents="attempt.documents"
                mode="result"
                :page-size="15"
                :show-counterparty="false"
              />
              <p v-else-if="!attempt.message" class="merge-job-documents__empty app-meta">
                В сохранённом ответе нет списка документов.
              </p>
            </section>
          </div>
        </el-collapse-item>
      </el-collapse>
    </template>
  </div>
</template>

<style scoped src="../../styles/components/merge/merge-job-documents.css"></style>
