<script setup>
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Right, Search } from '@element-plus/icons-vue'

import EmptyState from '../ui/EmptyState.vue'
import ErrorNotice from '../ui/ErrorNotice.vue'
import PageHeader from '../ui/PageHeader.vue'
import SectionPanel from '../ui/SectionPanel.vue'
import StatusBadge from '../ui/StatusBadge.vue'
import { formatDateTime, formatNumber } from '../../utils/format'

/**
 * `data` — `{ rows, total }` for the current route filters; null while the
 * history source is not connected.
 */
const props = defineProps({
  data: { type: Object, default: null },
  loading: { type: Boolean, default: false },
  loadError: { type: [String, Object], default: null }
})

const filterDefaults = {
  search: '',
  entityType: '',
  changeType: 'merge',
  dateFrom: '',
  dateTo: '',
  counterpartyId: '',
  page: 1,
  pageSize: 25
}

const route = useRoute()
const router = useRouter()
const filters = ref({ ...filterDefaults })
const rows = computed(() => (Array.isArray(props.data?.rows) ? props.data.rows.map(normalizeHistoryRow) : []))
const total = computed(() => Number(props.data?.total || 0))
const loading = computed(() => props.loading)
const unavailable = computed(() => !props.data && !props.loading && !props.loadError)

const totalPages = computed(() => {
  const pages = Math.ceil(total.value / filters.value.pageSize)
  return pages > 0 ? pages : 1
})

const entityTypeOptions = [
  { value: '', label: 'Все сущности' },
  { value: 'counterparty', label: 'Карточка КА' }
]

const changeTypeOptions = [
  { value: 'merge', label: 'Объединение' }
]

const quickTabs = [
  { value: 'merge', label: 'Объединения' }
]

watch(
  () => route.query,
  (query) => {
    filters.value = readFiltersFromRouteQuery(query)
  },
  { immediate: true }
)

// changeType has a single default option, so it is not treated as a user filter.
const hasActiveFilters = computed(() =>
  Boolean(
    filters.value.search ||
    filters.value.entityType ||
    filters.value.dateFrom ||
    filters.value.dateTo ||
    filters.value.counterpartyId
  )
)

const rangeLabel = computed(() => {
  if (!total.value) {
    return ''
  }
  const start = (filters.value.page - 1) * filters.value.pageSize + 1
  const end = Math.min(filters.value.page * filters.value.pageSize, total.value)
  return `${formatNumber(start)}–${formatNumber(end)} из ${formatNumber(total.value)}`
})

function buildRouteQuery() {
  const query = {}
  const search = filters.value.search.trim()

  if (search) {
    query.search = search
  }

  if (filters.value.entityType) {
    query.entityType = filters.value.entityType
  }

  if (filters.value.changeType) {
    query.changeType = filters.value.changeType
  }

  if (filters.value.dateFrom) {
    query.dateFrom = filters.value.dateFrom
  }

  if (filters.value.dateTo) {
    query.dateTo = filters.value.dateTo
  }

  if (filters.value.counterpartyId) {
    query.counterpartyId = filters.value.counterpartyId
  }

  if (filters.value.page > 1) {
    query.page = String(filters.value.page)
  }

  if (filters.value.pageSize !== filterDefaults.pageSize) {
    query.pageSize = String(filters.value.pageSize)
  }

  return query
}

function readFiltersFromRouteQuery(query) {
  return {
    search: normalizeQueryString(query.search),
    entityType: normalizeEnumQuery(query.entityType, entityTypeOptions.map((item) => item.value)),
    changeType: normalizeEnumQuery(query.changeType, changeTypeOptions.map((item) => item.value)),
    dateFrom: normalizeDateQuery(query.dateFrom),
    dateTo: normalizeDateQuery(query.dateTo),
    counterpartyId: normalizeQueryString(query.counterpartyId),
    page: normalizePositiveQuery(query.page, filterDefaults.page),
    pageSize: normalizePageSizeQuery(query.pageSize, filterDefaults.pageSize)
  }
}

async function replaceHistoryQuery() {
  await router.replace({
    name: 'moysklad-history-counterparties',
    query: buildRouteQuery()
  })
}

function normalizeHistoryRow(item) {
  return {
    id: Number(item?.id || 0),
    counterpartyId: String(item?.counterpartyId || ''),
    counterpartyName: String(item?.counterpartyName || ''),
    counterpartyArchived: Boolean(item?.counterpartyArchived),
    fieldName: String(item?.fieldName || ''),
    oldValue: normalizeHistoryValue(item?.oldValue),
    newValue: normalizeHistoryValue(item?.newValue),
    changeType: String(item?.changeType || ''),
    changedAt: String(item?.changedAt || ''),
    oldCounterpartyId: String(item?.oldCounterpartyId || ''),
    newCounterpartyId: String(item?.newCounterpartyId || '')
  }
}

function applyFilters() {
  filters.value.page = 1
  void replaceHistoryQuery()
}

function resetFilters() {
  filters.value = { ...filterDefaults }
  void replaceHistoryQuery()
}

function goToPage(page) {
  const normalizedPage = Math.max(1, Math.min(totalPages.value, Number(page || 1)))
  if (normalizedPage === filters.value.page) {
    return
  }

  filters.value.page = normalizedPage
  void replaceHistoryQuery()
}

function handleQuickTab(value) {
  filters.value.changeType = value
  filters.value.page = 1
  void replaceHistoryQuery()
}

function isQuickTabActive(value) {
  return filters.value.changeType === value
}

function openCounterparty(row) {
  if (!row.counterpartyId) {
    return
  }

  router.push({
    name: 'moysklad-counterparty',
    params: {
      id: row.counterpartyId
    },
    query: {
      ...buildRouteQuery(),
      returnTo: 'history-counterparties'
    }
  })
}

function clearCounterpartyFilter() {
  filters.value.counterpartyId = ''
  filters.value.page = 1
  void replaceHistoryQuery()
}

function normalizeQueryString(rawValue) {
  if (Array.isArray(rawValue)) {
    return String(rawValue[0] || '').trim()
  }

  return String(rawValue || '').trim()
}

function normalizeEnumQuery(rawValue, allowedValues) {
  const normalized = normalizeQueryString(rawValue)
  return allowedValues.includes(normalized) ? normalized : allowedValues[0] || ''
}

function normalizePositiveQuery(rawValue, fallback) {
  const parsed = Number(normalizeQueryString(rawValue))
  return Number.isFinite(parsed) && parsed > 0 ? Math.trunc(parsed) : fallback
}

function normalizePageSizeQuery(rawValue, fallback) {
  const parsed = normalizePositiveQuery(rawValue, fallback)
  return [25, 50, 100].includes(parsed) ? parsed : fallback
}

function normalizeDateQuery(rawValue) {
  const normalized = normalizeQueryString(rawValue)
  return /^\d{4}-\d{2}-\d{2}$/.test(normalized) ? normalized : ''
}

function fieldLabel(fieldName) {
  if (!fieldName) {
    return '—'
  }

  if (fieldName.startsWith('attribute.')) {
    return 'Поле'
  }

  switch (fieldName) {
    case 'name':
      return 'Наименование'
    case 'description':
      return 'Описание'
    case 'email':
      return 'Email'
    case 'phone':
      return 'Телефон'
    default:
      return fieldName
  }
}

function changeTypeLabel(changeType) {
  switch (changeType) {
    case 'merge':
      return 'Объединение'
    default:
      return 'Объединение'
  }
}

function displayValue(value, fallback = '—') {
  return value ? value : fallback
}

function normalizeHistoryValue(value) {
  if (value === null || value === undefined) {
    return ''
  }

  return String(value)
}

function displayHistoryValue(value) {
  const normalized = String(value || '').trim()
  if (!normalized || normalized === 'null') {
    return 'Пусто'
  }

  return extractMoyskladReferenceName(normalized) || normalized
}

function extractMoyskladReferenceName(value) {
  if (!value || !['{', '['].includes(value[0])) {
    return ''
  }

  try {
    const parsed = JSON.parse(value)
    return extractReferenceName(parsed)
  } catch {
    return ''
  }
}

function extractReferenceName(value) {
  if (Array.isArray(value)) {
    const names = value.map(extractReferenceName).filter(Boolean)
    return names.join(', ')
  }

  if (!value || typeof value !== 'object') {
    return ''
  }

  const name = typeof value.name === 'string' ? value.name.trim() : ''
  const meta = value.meta && typeof value.meta === 'object' ? value.meta : null
  if (name && meta && (meta.href || meta.uuidHref || meta.metadataHref || meta.type)) {
    return name
  }

  return ''
}
</script>

<template>
  <div class="app-page counterparty-history">
    <PageHeader
      title="История изменений"
      subtitle="Как менялись карточки контрагентов при объединениях"
    >
      <template v-if="total" #meta>
        <span class="app-meta app-nums">{{ formatNumber(total) }} записей</span>
      </template>
    </PageHeader>

    <SectionPanel class="counterparty-history__panel" flush>
      <el-tabs
        class="counterparty-history__tabs"
        :model-value="filters.changeType"
        @tab-change="handleQuickTab"
      >
        <el-tab-pane
          v-for="tab in quickTabs"
          :key="tab.value || 'all'"
          :label="tab.label"
          :name="tab.value"
        />
      </el-tabs>

      <el-form class="counterparty-history__filters" label-position="top" size="default" @submit.prevent="applyFilters">
        <el-form-item class="counterparty-history__field--wide" label="Поиск">
          <el-input
            v-model="filters.search"
            clearable
            :prefix-icon="Search"
            placeholder="Контрагент, поле или значение"
            @keydown.enter.prevent="applyFilters"
          />
        </el-form-item>

        <el-form-item label="Сущность">
          <el-select v-model="filters.entityType" placeholder="Все сущности">
            <el-option
              v-for="option in entityTypeOptions"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-select>
        </el-form-item>

        <el-form-item label="Тип изменения">
          <el-select v-model="filters.changeType">
            <el-option
              v-for="option in changeTypeOptions"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-select>
        </el-form-item>

        <el-form-item label="С даты">
          <el-date-picker
            v-model="filters.dateFrom"
            type="date"
            value-format="YYYY-MM-DD"
            format="DD.MM.YYYY"
            placeholder="дд.мм.гггг"
          />
        </el-form-item>

        <el-form-item label="По дату">
          <el-date-picker
            v-model="filters.dateTo"
            type="date"
            value-format="YYYY-MM-DD"
            format="DD.MM.YYYY"
            placeholder="дд.мм.гггг"
          />
        </el-form-item>

        <div class="counterparty-history__filter-actions">
          <el-button type="primary" @click="applyFilters">Применить</el-button>
          <el-button text @click="resetFilters">Сбросить</el-button>
        </div>
      </el-form>

      <div v-if="filters.counterpartyId" class="counterparty-history__scope">
        <span>Показана история одного контрагента</span>
        <el-button link type="primary" @click="clearCounterpartyFilter">Показать всех</el-button>
      </div>

      <div v-if="loading" class="counterparty-history__skeleton" aria-busy="true">
        <el-skeleton :rows="6" animated />
      </div>

      <div v-else-if="loadError" class="counterparty-history__state">
        <ErrorNotice :error="loadError" fallback="Не удалось загрузить историю изменений" />
      </div>

      <EmptyState
        v-else-if="unavailable"
        image="unavailable"
        title="Журнал изменений пока недоступен"
        description="История правок появится здесь после подключения сервиса журнала."
      />

      <EmptyState
        v-else-if="!rows.length && hasActiveFilters"
        image="search"
        title="По фильтрам ничего не найдено"
        description="Измените условия поиска или сбросьте фильтры."
      >
        <el-button size="small" @click="resetFilters">Сбросить фильтры</el-button>
      </EmptyState>

      <EmptyState
        v-else-if="!rows.length"
        image="history"
        title="История пока пуста"
        description="Здесь появятся изменения карточек после первого объединения контрагентов."
      >
        <el-button size="small" @click="router.push({ name: 'moysklad-duplicates' })">Найти дубли</el-button>
      </EmptyState>

      <el-table
        v-else
        class="counterparty-history__table"
        :data="rows"
        table-layout="fixed"
        row-key="id"
      >
        <el-table-column label="Дата" width="140">
          <template #default="{ row }">
            <span class="app-text-secondary app-nums">{{ formatDateTime(row.changedAt) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="Контрагент" min-width="220">
          <template #default="{ row }">
            <div class="counterparty-history__counterparty">
              <el-tooltip :content="displayValue(row.counterpartyName, 'Контрагент')" placement="top" :show-after="500">
                <button type="button" class="counterparty-history__link app-truncate" @click="openCounterparty(row)">
                  {{ displayValue(row.counterpartyName, 'Контрагент') }}
                </button>
              </el-tooltip>
              <StatusBadge v-if="row.counterpartyArchived" size="sm" tone="neutral" label="Архивный" />
            </div>
          </template>
        </el-table-column>
        <el-table-column label="Поле" width="150">
          <template #default="{ row }">{{ fieldLabel(row.fieldName) }}</template>
        </el-table-column>
        <el-table-column label="Было" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">
            <span class="counterparty-history__old" :class="{ 'counterparty-history__empty': !row.oldValue }">
              {{ displayHistoryValue(row.oldValue) }}
            </span>
          </template>
        </el-table-column>
        <el-table-column width="28" align="center">
          <template #default>
            <el-icon class="counterparty-history__arrow" aria-hidden="true"><Right /></el-icon>
          </template>
        </el-table-column>
        <el-table-column label="Стало" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">
            <span :class="{ 'counterparty-history__empty': !row.newValue }">{{ displayHistoryValue(row.newValue) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="Тип" width="130">
          <template #default="{ row }">
            <StatusBadge size="sm" tone="primary" :label="changeTypeLabel(row.changeType)" />
          </template>
        </el-table-column>
      </el-table>

      <template v-if="rows.length" #footer>
        <span class="app-meta app-nums">{{ rangeLabel }}</span>
        <div class="counterparty-history__pagination">
          <el-select v-model="filters.pageSize" class="counterparty-history__page-size" size="small" @change="applyFilters">
            <el-option :value="25" label="25 на странице" />
            <el-option :value="50" label="50 на странице" />
            <el-option :value="100" label="100 на странице" />
          </el-select>
          <el-pagination
            :current-page="filters.page"
            :page-size="filters.pageSize"
            :total="total"
            layout="prev, pager, next"
            :pager-count="5"
            :disabled="loading"
            size="small"
            @current-change="goToPage"
          />
        </div>
      </template>
    </SectionPanel>
  </div>
</template>

<style scoped src="../../styles/components/counterparty-change-history-panel.css"></style>
