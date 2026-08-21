<script setup>
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

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
const rows = ref([])
const total = ref(0)
const loading = ref(false)
const error = ref('Раздел временно недоступен')

const totalPages = computed(() => {
  const pages = Math.ceil(total.value / filters.value.pageSize)
  return pages > 0 ? pages : 1
})

const entityTypeOptions = [
  { value: '', label: 'Все сущности' },
  { value: 'counterparty', label: 'Карточка КА' }
]

const changeTypeOptions = [
  { value: 'merge', label: 'Merge' }
]

const quickTabs = [
  { value: 'merge', label: 'Merge' }
]

watch(
  () => route.query,
  (query) => {
    filters.value = readFiltersFromRouteQuery(query)
  },
  { immediate: true }
)

const hasActiveFilters = computed(() =>
  Boolean(
    filters.value.search ||
    filters.value.entityType ||
    filters.value.changeType ||
    filters.value.dateFrom ||
    filters.value.dateTo ||
    filters.value.counterpartyId
  )
)

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
      return 'Merge'
    default:
      return 'Merge'
  }
}

function changeTypeClass(changeType) {
  return {
    'counterparty-history__badge': true,
    'counterparty-history__badge--merge': true
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
  <section class="counterparty-history">
    <header class="counterparty-history__header">
      <div>
        <h1>История изменений КА</h1>
        <p>Поиск по merge-операциям и изменениям карточки контрагента</p>
      </div>
      <el-tag type="info" effect="plain">Всего записей: {{ total }}</el-tag>
    </header>

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

    <el-card class="counterparty-history__filters" shadow="never">
      <el-form class="counterparty-history__filter-grid" label-position="top" size="small">
        <el-form-item class="counterparty-history__field--wide" label="Поиск">
          <el-input
            v-model="filters.search"
            clearable
            placeholder="Имя КА, поле, тип изменения"
            @keydown.enter.prevent="applyFilters"
          />
        </el-form-item>

        <el-form-item label="Сущность">
          <el-select v-model="filters.entityType">
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

        <el-form-item label="Дата от">
          <el-date-picker
            v-model="filters.dateFrom"
            type="date"
            value-format="YYYY-MM-DD"
            format="DD.MM.YYYY"
            placeholder="Выберите дату"
          />
        </el-form-item>

        <el-form-item label="Дата до">
          <el-date-picker
            v-model="filters.dateTo"
            type="date"
            value-format="YYYY-MM-DD"
            format="DD.MM.YYYY"
            placeholder="Выберите дату"
          />
        </el-form-item>

        <el-form-item label="На странице">
          <el-select v-model="filters.pageSize" @change="applyFilters">
            <el-option :value="25" label="25" />
            <el-option :value="50" label="50" />
            <el-option :value="100" label="100" />
          </el-select>
        </el-form-item>

        <div class="counterparty-history__filter-actions">
          <el-button type="primary" @click="applyFilters">Применить</el-button>
          <el-button plain @click="resetFilters">Сбросить</el-button>
        </div>
      </el-form>
    </el-card>

    <el-alert
      v-if="filters.counterpartyId"
      title="Показана история по выбранному КА"
      type="info"
      :closable="false"
      show-icon
    >
      <template #default>
        <el-button link type="primary" @click="clearCounterpartyFilter">Сбросить фильтр</el-button>
      </template>
    </el-alert>

    <el-skeleton v-if="loading" :rows="4" animated />
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />

    <el-empty
      v-if="!loading && !rows.length"
      :description="hasActiveFilters ? 'По текущим фильтрам записи не найдены' : 'История изменений пока не найдена'"
      :image-size="80"
    />

    <el-table
      v-else
      class="counterparty-history__table"
      :data="rows"
      border
      table-layout="auto"
      empty-text="История изменений пока не найдена"
    >
      <el-table-column label="Дата" min-width="140">
        <template #default="{ row }">{{ displayValue(row.changedAt) }}</template>
      </el-table-column>
      <el-table-column label="Контрагент" min-width="180">
        <template #default="{ row }">
          <el-button class="counterparty-history__counterparty" link type="primary" @click="openCounterparty(row)">
            {{ displayValue(row.counterpartyName, 'Контрагент') }}
          </el-button>
          <el-tag v-if="row.counterpartyArchived" type="info" size="small">Архивный</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="Поле" min-width="120">
        <template #default="{ row }">{{ fieldLabel(row.fieldName) }}</template>
      </el-table-column>
      <el-table-column label="Тип" width="90">
        <template #default="{ row }">
          <el-tag type="primary" size="small" effect="light">{{ changeTypeLabel(row.changeType) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="Старое значение" min-width="180">
        <template #default="{ row }">{{ displayHistoryValue(row.oldValue) }}</template>
      </el-table-column>
      <el-table-column label="Новое значение" min-width="180">
        <template #default="{ row }">{{ displayHistoryValue(row.newValue) }}</template>
      </el-table-column>
    </el-table>

    <footer class="counterparty-history__pagination">
      <span>Страница {{ filters.page }} из {{ totalPages }}</span>
      <el-pagination
        :current-page="filters.page"
        :page-size="filters.pageSize"
        :total="total"
        layout="prev, next"
        :disabled="loading"
        @current-change="goToPage"
      />
    </footer>
  </section>
</template>

<style scoped src="../../styles/components/counterparty-change-history-panel.css"></style>
