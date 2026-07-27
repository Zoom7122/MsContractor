<script setup>
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { getCounterpartyChangeHistory } from '../../api/counterpartyHistory'

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
const error = ref(null)

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
    void loadHistory()
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

async function loadHistory() {
  if (loading.value) {
    return
  }

  loading.value = true
  error.value = null

  try {
    const response = await getCounterpartyChangeHistory(buildApiQuery())
    rows.value = Array.isArray(response?.items) ? response.items.map(normalizeHistoryRow) : []
    total.value = Number(response?.total || 0)
    filters.value.page = Number(response?.page || filters.value.page || 1)
    filters.value.pageSize = Number(response?.pageSize || filters.value.pageSize || 25)
  } catch (requestError) {
    error.value = requestError.message || 'Не удалось загрузить историю изменений КА'
  } finally {
    loading.value = false
  }
}

function buildApiQuery() {
  const query = {
    page: filters.value.page,
    pageSize: filters.value.pageSize,
    sortBy: 'changedAt',
    sortDirection: 'desc'
  }

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
    query.dateFrom = `${filters.value.dateFrom}T00:00:00`
  }

  if (filters.value.dateTo) {
    query.dateTo = `${filters.value.dateTo}T23:59:59`
  }

  if (filters.value.counterpartyId) {
    query.counterpartyId = filters.value.counterpartyId
  }

  return query
}

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

      <div class="counterparty-history__summary">
        <span>Всего записей</span>
        <strong>{{ total }}</strong>
      </div>
    </header>

    <section class="counterparty-history__tabs">
      <button
        v-for="tab in quickTabs"
        :key="tab.value || 'all'"
        class="counterparty-history__tab"
        :class="{ 'counterparty-history__tab--active': isQuickTabActive(tab.value) }"
        type="button"
        @click="handleQuickTab(tab.value)"
      >
        {{ tab.label }}
      </button>
    </section>

    <section class="counterparty-history__filters">
      <label class="counterparty-history__field counterparty-history__field--wide">
        <span>Поиск</span>
        <input
          v-model="filters.search"
          type="search"
          placeholder="Имя КА, поле, тип изменения"
          @keydown.enter.prevent="applyFilters"
        />
      </label>

      <label class="counterparty-history__field">
        <span>Сущность</span>
        <select v-model="filters.entityType">
          <option v-for="option in entityTypeOptions" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>

      <label class="counterparty-history__field">
        <span>Тип изменения</span>
        <select v-model="filters.changeType">
          <option v-for="option in changeTypeOptions" :key="option.value" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>

      <label class="counterparty-history__field">
        <span>Дата от</span>
        <input v-model="filters.dateFrom" type="date" />
      </label>

      <label class="counterparty-history__field">
        <span>Дата до</span>
        <input v-model="filters.dateTo" type="date" />
      </label>

      <label class="counterparty-history__field">
        <span>На странице</span>
        <select v-model.number="filters.pageSize" @change="applyFilters">
          <option :value="25">25</option>
          <option :value="50">50</option>
          <option :value="100">100</option>
        </select>
      </label>

      <div class="counterparty-history__filter-actions">
        <button class="counterparty-history__button counterparty-history__button--primary" type="button" @click="applyFilters">
          Применить
        </button>
        <button class="counterparty-history__button counterparty-history__button--ghost" type="button" @click="resetFilters">
          Сбросить
        </button>
      </div>
    </section>

    <div v-if="filters.counterpartyId" class="counterparty-history__active-filter">
      <span>Показана история по выбранному КА</span>
      <button type="button" @click="clearCounterpartyFilter">Сбросить</button>
    </div>

    <p v-if="loading" class="counterparty-history__message">Загружаем историю изменений...</p>
    <p v-if="error" class="counterparty-history__error">{{ error }}</p>

    <div v-if="!loading && !rows.length" class="counterparty-history__empty">
      {{ hasActiveFilters ? 'По текущим фильтрам записи не найдены.' : 'История изменений пока не найдена.' }}
    </div>

    <div v-else class="counterparty-history__table-wrap">
      <table class="counterparty-history__table">
        <thead>
          <tr>
            <th>Дата</th>
            <th>Контрагент</th>
            <th>Поле</th>
            <th>Тип</th>
            <th>Старое значение</th>
            <th>Новое значение</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.id">
            <td>{{ displayValue(row.changedAt) }}</td>
            <td>
              <button class="counterparty-history__counterparty" type="button" @click="openCounterparty(row)">
                <strong>{{ displayValue(row.counterpartyName, 'Контрагент') }}</strong>
                <span v-if="row.counterpartyArchived" class="counterparty-history__archived">Архивный</span>
              </button>
            </td>
            <td>
              <div class="counterparty-history__value-block">
                <strong>{{ fieldLabel(row.fieldName) }}</strong>
              </div>
            </td>
            <td>
              <span :class="changeTypeClass(row.changeType)">
                {{ changeTypeLabel(row.changeType) }}
              </span>
            </td>
            <td>
              <div class="counterparty-history__value-block">
                <span>{{ displayHistoryValue(row.oldValue) }}</span>
              </div>
            </td>
            <td>
              <div class="counterparty-history__value-block">
                <span>{{ displayHistoryValue(row.newValue) }}</span>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <footer class="counterparty-history__pagination">
      <div class="counterparty-history__pagination-info">
        Страница {{ filters.page }} из {{ totalPages }}
      </div>

      <div class="counterparty-history__pagination-actions">
        <button
          class="counterparty-history__button counterparty-history__button--ghost"
          type="button"
          :disabled="filters.page <= 1 || loading"
          @click="goToPage(filters.page - 1)"
        >
          Назад
        </button>
        <button
          class="counterparty-history__button counterparty-history__button--ghost"
          type="button"
          :disabled="filters.page >= totalPages || loading"
          @click="goToPage(filters.page + 1)"
        >
          Вперёд
        </button>
      </div>
    </footer>
  </section>
</template>

<style scoped>
.counterparty-history {
  min-height: 100%;
  padding: 24px 32px 32px;
  background:
    radial-gradient(circle at top left, rgba(45, 108, 223, 0.12), transparent 30%),
    #f4f7fb;
}

.counterparty-history__header {
  display: flex;
  justify-content: space-between;
  gap: 20px;
  margin-bottom: 20px;
  padding: 24px 26px;
  background: linear-gradient(135deg, #08162d, #2351a0);
  border-radius: 18px;
  box-shadow: 0 18px 36px rgba(15, 35, 80, 0.18);
}

.counterparty-history__header h1 {
  margin: 0;
  color: #ffffff;
  font-size: 28px;
  font-weight: 800;
  line-height: 1.2;
}

.counterparty-history__header p {
  margin: 8px 0 0;
  color: rgba(227, 235, 255, 0.88);
  font-size: 15px;
  line-height: 1.5;
}

.counterparty-history__summary {
  display: grid;
  gap: 6px;
  min-width: 140px;
  padding: 14px 16px;
  color: #d9e7ff;
  background: rgba(255, 255, 255, 0.08);
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 14px;
}

.counterparty-history__summary span {
  font-size: 12px;
  font-weight: 700;
}

.counterparty-history__summary strong {
  color: #ffffff;
  font-size: 24px;
  font-weight: 800;
}

.counterparty-history__tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin-bottom: 16px;
}

.counterparty-history__tab {
  min-height: 38px;
  padding: 0 16px;
  color: #335c99;
  background: #ffffff;
  border: 1px solid #d4e0f3;
  border-radius: 999px;
  font-size: 13px;
  font-weight: 800;
  cursor: pointer;
  transition:
    background 0.18s ease,
    color 0.18s ease,
    border-color 0.18s ease,
    transform 0.18s ease;
}

.counterparty-history__tab:hover {
  transform: translateY(-1px);
}

.counterparty-history__tab--active {
  color: #ffffff;
  background: linear-gradient(135deg, #1d4ed8, #2563eb);
  border-color: #1d4ed8;
  box-shadow: 0 10px 20px rgba(37, 99, 235, 0.18);
}

.counterparty-history__filters {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: 14px;
  margin-bottom: 18px;
  padding: 20px;
  background: #ffffff;
  border: 1px solid #dfe7f3;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(15, 35, 80, 0.06);
}

.counterparty-history__field {
  display: grid;
  gap: 8px;
}

.counterparty-history__field--wide {
  grid-column: span 2;
}

.counterparty-history__field span {
  color: #50648f;
  font-size: 13px;
  font-weight: 700;
}

.counterparty-history__field input,
.counterparty-history__field select {
  min-height: 42px;
  padding: 10px 12px;
  color: #0f1b3d;
  background: #f8fbff;
  border: 1px solid #d8e4f5;
  border-radius: 10px;
}

.counterparty-history__filter-actions {
  display: flex;
  align-items: end;
  gap: 10px;
}

.counterparty-history__button {
  min-height: 42px;
  padding: 10px 16px;
  border: 1px solid transparent;
  border-radius: 10px;
  font-size: 14px;
  font-weight: 700;
  cursor: pointer;
}

.counterparty-history__button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.counterparty-history__button--primary {
  color: #ffffff;
  background: #2d6cdf;
  border-color: #2d6cdf;
}

.counterparty-history__button--ghost {
  color: #2d6cdf;
  background: #ffffff;
  border-color: #9bb8ef;
}

.counterparty-history__active-filter {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  margin-bottom: 16px;
  padding: 12px 14px;
  color: #335c99;
  background: #edf4ff;
  border: 1px solid #cfe0fb;
  border-radius: 12px;
  font-size: 13px;
}

.counterparty-history__active-filter strong {
  color: #0f1b3d;
}

.counterparty-history__active-filter button {
  min-height: 30px;
  padding: 0 12px;
  color: #2d6cdf;
  background: #ffffff;
  border: 1px solid #9bb8ef;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 700;
  cursor: pointer;
}

.counterparty-history__message,
.counterparty-history__error {
  margin: 0 0 16px;
  font-size: 14px;
  line-height: 1.4;
}

.counterparty-history__error {
  color: #d92d3f;
}

.counterparty-history__empty,
.counterparty-history__table-wrap {
  background: #ffffff;
  border: 1px solid #dfe7f3;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(15, 35, 80, 0.06);
}

.counterparty-history__empty {
  padding: 24px;
  color: #64759b;
  font-size: 14px;
}

.counterparty-history__table-wrap {
  overflow: auto;
}

.counterparty-history__table {
  width: 100%;
  min-width: 1120px;
  border-collapse: collapse;
}

.counterparty-history__table th,
.counterparty-history__table td {
  padding: 14px 12px;
  vertical-align: top;
  text-align: left;
  border-bottom: 1px solid #dfe7f3;
}

.counterparty-history__table th {
  color: #50648f;
  font-size: 13px;
  font-weight: 800;
  background: #f7f9fd;
}

.counterparty-history__table td {
  color: #0f1b3d;
  font-size: 13px;
  line-height: 1.45;
}

.counterparty-history__counterparty,
.counterparty-history__value-block {
  display: grid;
  gap: 4px;
}

.counterparty-history__counterparty {
  width: 100%;
  padding: 0;
  text-align: left;
  background: transparent;
  border: 0;
  cursor: pointer;
}

.counterparty-history__counterparty strong,
.counterparty-history__value-block strong {
  color: #0f1b3d;
  font-size: 13px;
  font-weight: 800;
}

.counterparty-history__counterparty:hover strong {
  color: #1d4ed8;
}

.counterparty-history__counterparty span,
.counterparty-history__value-block span {
  color: #64759b;
  white-space: pre-wrap;
  word-break: break-word;
}

.counterparty-history__archived {
  color: #9b5d00 !important;
}

.counterparty-history__badge {
  display: inline-flex;
  min-height: 28px;
  align-items: center;
  padding: 0 12px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 800;
}

.counterparty-history__badge--merge {
  color: #7b4bc4;
  background: #f0e7ff;
}

.counterparty-history__badge--manual {
  color: #157347;
  background: #e5f7ee;
}

.counterparty-history__badge--api {
  color: #1d4ed8;
  background: #e8f0ff;
}

.counterparty-history__badge--update {
  color: #50648f;
  background: #edf2fb;
}

.counterparty-history__pagination {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: center;
  margin-top: 16px;
  padding: 0 4px;
}

.counterparty-history__pagination-info {
  color: #50648f;
  font-size: 14px;
  font-weight: 700;
}

.counterparty-history__pagination-actions {
  display: flex;
  gap: 10px;
}

@media (max-width: 1200px) {
  .counterparty-history__filters {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }

  .counterparty-history__field--wide {
    grid-column: span 3;
  }
}

@media (max-width: 760px) {
  .counterparty-history {
    padding: 20px;
  }

  .counterparty-history__header,
  .counterparty-history__pagination {
    flex-direction: column;
    align-items: stretch;
  }

  .counterparty-history__filters {
    grid-template-columns: 1fr;
  }

  .counterparty-history__field--wide {
    grid-column: span 1;
  }

  .counterparty-history__filter-actions,
  .counterparty-history__pagination-actions {
    flex-direction: column;
  }
}
</style>
