<script setup>
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getSyncRunErrors } from '../../api/sync-runs'
import { useSyncRunPage } from '../../composables/useSyncRunPage'
import { SYNC_PAGE_SIZE, syncPage, syncRunIsActive, syncRunStatus, syncModeLabel } from '../../domain/sync'
import { formatDateTime, formatNumber } from '../../utils/format'
import PageHeader from '../ui/PageHeader.vue'
import SectionPanel from '../ui/SectionPanel.vue'
import ErrorNotice from '../ui/ErrorNotice.vue'
import StatusBadge from '../ui/StatusBadge.vue'

const props = defineProps({ syncRunId: { type: String, required: true } })
const route = useRoute()
const router = useRouter()
const page = computed(() => syncPage(route.query.page))
const { data, error, loading, reload } = useSyncRunPage(
  () => [props.syncRunId, page.value],
  (signal) => getSyncRunErrors(props.syncRunId, page.value, SYNC_PAGE_SIZE, signal),
  (response) => syncRunIsActive(response?.run)
)
const run = computed(() => data.value?.run)
const subtitle = computed(() => run.value
  ? `${syncModeLabel(run.value)} · ${formatDateTime(run.value.startedAt || run.value.createdAt)}`
  : 'Ошибки выбранного запуска')
const fallback = computed(() => error.value?.status === 404
  ? 'Запуск не найден или недоступен' : 'Не удалось загрузить ошибки синхронизации')
function changePage(value) {
  void router.replace({ query: { ...route.query, page: String(value) } })
}
function back() {
  void router.push({ name: 'moysklad-sync', query: { page: String(syncPage(route.query.fromPage)) } })
}
</script>

<template>
  <div class="app-page">
    <PageHeader title="Ошибки синхронизации" :subtitle="subtitle" />
    <SectionPanel title="Журнал ошибок">
      <template #actions>
        <StatusBadge v-if="run" :meta="syncRunStatus(run)" />
        <el-button :loading="loading" @click="reload">Обновить</el-button>
        <el-button @click="back">К истории запусков</el-button>
      </template>
      <ErrorNotice v-if="error" :error="error" :fallback="fallback" retryable @retry="reload" />
      <template v-if="run">
        <p class="sync-errors-summary">Ошибок за запуск: <strong class="app-nums">{{ formatNumber(run.errorsCount) }}</strong></p>
        <el-alert v-if="run.errorMessage" :title="run.errorMessage" type="error" :closable="false" show-icon />
      </template>
      <el-table v-if="data || loading" v-loading="loading" :data="data?.items || []" empty-text="Ошибок в этом запуске не зарегистрировано">
        <el-table-column label="Время" min-width="180">
          <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="ID КА" min-width="310">
          <template #default="{ row }"><span :class="{ 'app-mono': row.counterpartyId }">{{ row.counterpartyId || 'Общая ошибка запуска' }}</span></template>
        </el-table-column>
        <el-table-column label="Ошибка" min-width="300">
          <template #default="{ row }"><span class="sync-error-message">{{ row.errorMessage }}</span></template>
        </el-table-column>
      </el-table>
      <template v-if="data" #footer>
        <el-pagination :current-page="page" :page-size="SYNC_PAGE_SIZE" :total="data.total"
          layout="prev, pager, next, total" :hide-on-single-page="page === 1" @current-change="changePage" />
      </template>
    </SectionPanel>
  </div>
</template>

<style scoped>
.sync-errors-summary { margin: 0 0 16px; }
.sync-error-message { white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
