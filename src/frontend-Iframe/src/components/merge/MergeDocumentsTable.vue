<script setup>
import { computed, ref, watch } from 'vue'

import EmptyState from '../ui/EmptyState.vue'
import StatusBadge from '../ui/StatusBadge.vue'
import { DOCUMENT_ACTIONS, documentResultMeta, documentTypeLabel } from '../../domain/merge'
import { formatDateTime, formatNumber } from '../../utils/format'

/**
 * Documents affected by a merge.
 * `preview` — what will happen (action per document);
 * `result` — what happened (action + per-document result and error text).
 */
const props = defineProps({
  documents: { type: Array, default: () => [] },
  mode: { type: String, default: 'preview' },
  pageSize: { type: Number, default: 20 },
  showCounterparty: { type: Boolean, default: true }
})

const filter = ref('all')
const page = ref(1)

const counts = computed(() => {
  const result = { all: props.documents.length, reassign: 0, recreate: 0, failed: 0, skipped: 0 }
  for (const document of props.documents) {
    result[document.action] = (result[document.action] || 0) + 1
    if (document.result === 'failed') result.failed += 1
    if (document.result === 'skipped') result.skipped += 1
  }
  return result
})

const filters = computed(() => {
  const items = [
    { value: 'all', label: 'Все', count: counts.value.all },
    { value: 'reassign', label: 'Перепривязка', count: counts.value.reassign },
    { value: 'recreate', label: 'Пересоздание', count: counts.value.recreate }
  ]
  if (props.mode === 'result') {
    items.push(
      { value: 'failed', label: 'С ошибкой', count: counts.value.failed },
      { value: 'skipped', label: 'Пропущены', count: counts.value.skipped }
    )
  }
  return items.filter((item) => item.value === 'all' || item.count > 0)
})

const filtered = computed(() => {
  switch (filter.value) {
    case 'reassign':
    case 'recreate':
      return props.documents.filter((document) => document.action === filter.value)
    case 'failed':
    case 'skipped':
      return props.documents.filter((document) => document.result === filter.value)
    default:
      return props.documents
  }
})

const pageRows = computed(() => {
  const start = (page.value - 1) * props.pageSize
  return filtered.value.slice(start, start + props.pageSize)
})

const hasSums = computed(() => props.documents.some((document) => document.sum !== null && document.sum !== undefined))

watch(filter, () => {
  page.value = 1
})

watch(
  () => props.documents,
  () => {
    page.value = 1
    if (!filters.value.some((item) => item.value === filter.value)) {
      filter.value = 'all'
    }
  }
)

function formatSum(value) {
  if (value === null || value === undefined || value === '') {
    return '—'
  }
  return `${formatNumber(Math.round(Number(value) / 100))} ₽`
}

function rowClassName({ row }) {
  return row.result === 'failed' ? 'merge-documents__row--failed' : ''
}
</script>

<template>
  <div class="merge-documents">
    <div class="merge-documents__toolbar">
      <el-radio-group v-model="filter" size="small" class="merge-documents__filter" aria-label="Фильтр документов">
        <el-radio-button v-for="item in filters" :key="item.value" :value="item.value">
          {{ item.label }}
          <span class="merge-documents__filter-count app-nums">{{ formatNumber(item.count) }}</span>
        </el-radio-button>
      </el-radio-group>
    </div>

    <el-table
      v-if="filtered.length"
      class="merge-documents__table"
      :data="pageRows"
      :row-class-name="rowClassName"
      table-layout="fixed"
      row-key="id"
    >
      <el-table-column label="Тип" min-width="170">
        <template #default="{ row }">
          <span class="merge-documents__type">{{ documentTypeLabel(row.type) }}</span>
        </template>
      </el-table-column>

      <el-table-column label="Документ" min-width="140">
        <template #default="{ row }">
          <div class="merge-documents__doc">
            <span class="merge-documents__number">№ {{ row.name || '—' }}</span>
            <span class="merge-documents__doc-meta app-meta">{{ formatDateTime(row.moment) }}<template v-if="row.stateName"> · {{ row.stateName }}</template></span>
          </div>
        </template>
      </el-table-column>

      <el-table-column v-if="showCounterparty" label="Исходный контрагент" min-width="170" show-overflow-tooltip>
        <template #default="{ row }">{{ row.counterpartyName || '—' }}</template>
      </el-table-column>

      <el-table-column v-if="hasSums" label="Сумма" width="120" align="right">
        <template #default="{ row }">
          <span class="app-nums">{{ formatSum(row.sum) }}</span>
        </template>
      </el-table-column>

      <el-table-column label="Действие" width="150">
        <template #default="{ row }">
          <el-tooltip :content="DOCUMENT_ACTIONS[row.action]?.hint" placement="top" :show-after="400">
            <StatusBadge size="sm" :meta="DOCUMENT_ACTIONS[row.action] || DOCUMENT_ACTIONS.reassign" />
          </el-tooltip>
        </template>
      </el-table-column>

      <el-table-column v-if="mode === 'result'" label="Результат" min-width="200">
        <template #default="{ row }">
          <div class="merge-documents__result">
            <StatusBadge size="sm" :meta="documentResultMeta(row.result)" />
            <el-tooltip v-if="row.errorMessage" :content="row.errorMessage" placement="top" :show-after="300">
              <span class="merge-documents__result-text app-clamp-2" :class="`merge-documents__result-text--${row.result}`">
                {{ row.errorMessage }}
              </span>
            </el-tooltip>
          </div>
        </template>
      </el-table-column>
    </el-table>

    <EmptyState
      v-else
      size="sm"
      image="documents"
      title="Нет документов в этой категории"
      description="Выберите другой фильтр, чтобы увидеть остальные документы."
    />

    <div v-if="filtered.length > pageSize" class="merge-documents__pagination">
      <span class="app-meta">
        {{ (page - 1) * pageSize + 1 }}–{{ Math.min(page * pageSize, filtered.length) }} из {{ formatNumber(filtered.length) }}
      </span>
      <el-pagination
        v-model:current-page="page"
        :page-size="pageSize"
        :total="filtered.length"
        layout="prev, pager, next"
        size="small"
        :pager-count="5"
      />
    </div>
  </div>
</template>

<style scoped src="../../styles/components/merge/merge-documents-table.css"></style>
