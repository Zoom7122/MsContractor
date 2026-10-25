<script setup>
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getSyncRuns } from '../api/sync-runs'
import { useSyncRunPage } from '../composables/useSyncRunPage'
import { SYNC_PAGE_SIZE, syncPage, syncRunIsActive, syncRunStatus, syncModeLabel } from '../domain/sync'
import { formatDateTime, formatNumber } from '../utils/format'
import PageHeader from '../components/ui/PageHeader.vue'
import SectionPanel from '../components/ui/SectionPanel.vue'
import ErrorNotice from '../components/ui/ErrorNotice.vue'
import StatusBadge from '../components/ui/StatusBadge.vue'

const route = useRoute()
const router = useRouter()
const page = computed(() => syncPage(route.query.page))
const { data, error, loading, reload } = useSyncRunPage(
  () => page.value,
  (signal) => getSyncRuns(page.value, SYNC_PAGE_SIZE, signal),
  (response) => response?.items?.some(syncRunIsActive) || false
)
const rows = computed(() => data.value?.items || [])

function changePage(value) {
  void router.replace({ query: { ...route.query, page: String(value) } })
}
</script>

<template>
  <div class="app-page sync-runs">
    <PageHeader title="Синхронизация" subtitle="История загрузки контрагентов из МоегоСклада" />
    <SectionPanel title="Запуски синхронизации" subtitle="Нажмите на число ошибок, чтобы открыть подробности запуска">
      <template #actions>
        <el-button :loading="loading" @click="reload">Обновить</el-button>
        <el-button type="primary" @click="router.push({ name: 'moysklad-overview' })">Открыть обзор</el-button>
      </template>
      <ErrorNotice v-if="error" :error="error" fallback="Не удалось загрузить историю синхронизации" retryable @retry="reload" />
      <el-table v-if="data || loading" v-loading="loading" :data="rows" empty-text="Запусков синхронизации пока нет">
        <el-table-column label="Запущена" min-width="180">
          <template #default="{ row }">{{ formatDateTime(row.startedAt || row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="Режим" min-width="170">
          <template #default="{ row }">{{ syncModeLabel(row) }}</template>
        </el-table-column>
        <el-table-column label="Статус" min-width="220">
          <template #default="{ row }"><StatusBadge :meta="syncRunStatus(row)" /></template>
        </el-table-column>
        <el-table-column label="Обработано" min-width="130" align="right">
          <template #default="{ row }"><span class="app-nums">{{ formatNumber(row.processedCount) }}</span></template>
        </el-table-column>
        <el-table-column label="Всего" min-width="100" align="right">
          <template #default="{ row }"><span class="app-nums">{{ formatNumber(row.totalCount) }}</span></template>
        </el-table-column>
        <el-table-column label="Ошибок" min-width="110" align="right">
          <template #default="{ row }">
            <RouterLink v-if="row.errorsCount > 0" class="sync-error-link app-nums"
              :to="{ name: 'moysklad-sync-errors', params: { syncRunId: row.id }, query: { fromPage: String(page) } }"
              :aria-label="`Ошибок: ${row.errorsCount}. Запуск от ${formatDateTime(row.startedAt || row.createdAt)}`">
              {{ formatNumber(row.errorsCount) }}
            </RouterLink>
            <span v-else class="app-nums">0</span>
          </template>
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
.sync-error-link { color: var(--el-color-danger); text-decoration: underline; font-weight: 600; }
</style>
