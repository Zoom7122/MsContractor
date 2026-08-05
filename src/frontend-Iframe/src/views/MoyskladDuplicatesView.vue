<script setup>
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

const props = defineProps({
  duplicatesData: {
    type: Object,
    default: null
  }
})

const fieldOptions = [
  { value: 'name', label: 'Наименование' },
  { value: 'email', label: 'Email' },
  { value: 'phone', label: 'Телефон' }
]

const defaultFilters = {
  fields: ['name'],
  search: ''
}

const route = useRoute()
const router = useRouter()
const filters = ref(readFiltersFromRoute(route.query))
const loading = ref(false)
const error = ref(props.duplicatesData ? null : 'Раздел временно недоступен')
const mergeMessage = ref(readMergeMessageFromRoute(route.query))
const groupsTruncated = ref(false)
const groups = ref([])
const selectedGroupKey = ref(null)
const selectedCounterpartyIds = ref([])
let pendingGroupKeyFromRoute = normalizeQueryValue(route.query.group)
let preserveMergeMessageOnNextLoad = Boolean(mergeMessage.value)

const selectedGroup = computed(() => groups.value.find((group) => group.key === selectedGroupKey.value) || null)

const totalCounterpartiesInGroups = computed(() =>
  groups.value.reduce((sum, group) => sum + (group.counterparties?.length || 0), 0)
)

const selectedFieldsLabel = computed(() =>
  filters.value.fields
    .map((field) => fieldOptions.find((item) => item.value === field)?.label || field)
    .join(', ')
)

const canGoToMerge = computed(() => selectedCounterpartyIds.value.length >= 2)
const selectedGroupCounterpartyIds = computed(() => selectedGroup.value?.counterparties?.map((item) => item.id) || [])
const allSelectedInGroup = computed(() =>
  selectedGroupCounterpartyIds.value.length > 0 &&
  selectedGroupCounterpartyIds.value.every((id) => selectedCounterpartyIds.value.includes(id))
)

watch(
  () => props.duplicatesData,
  (value) => {
    if (!value) {
      return
    }

    applyDuplicateResponse(value)
  },
  { immediate: true }
)

function loadDuplicateGroups() {
  if (!preserveMergeMessageOnNextLoad) {
    mergeMessage.value = null
  }
  preserveMergeMessageOnNextLoad = false

  if (!filters.value.fields.length) {
    error.value = 'Выберите хотя бы одно поле поиска'
    return
  }

  error.value = 'Раздел временно недоступен'
}

function applyDuplicateResponse(response) {
  groups.value = Array.isArray(response?.groups) ? response.groups.map(normalizeGroup) : []
  groupsTruncated.value = Boolean(response?.groupsTruncated)

  if (Array.isArray(response?.fields) && response.fields.length) {
    filters.value.fields = response.fields.filter((field) => fieldOptions.some((item) => item.value === field))
  }

  selectedCounterpartyIds.value = []
  selectedGroupKey.value = resolveInitialGroupKey(groups.value)
  pendingGroupKeyFromRoute = ''
}

function normalizeGroup(group) {
  return {
    key: String(group?.key || ''),
    matchCount: Number(group?.matchCount || 0),
    itemsTruncated: Boolean(group?.itemsTruncated),
    values: {
      name: String(group?.values?.name || ''),
      email: String(group?.values?.email || ''),
      phone: String(group?.values?.phone || '')
    },
    counterparties: Array.isArray(group?.counterparties)
      ? group.counterparties.map((item) => ({
          id: String(item?.id || ''),
          name: String(item?.name || ''),
          description: String(item?.description || ''),
          email: String(item?.email || ''),
          phone: String(item?.phone || ''),
          archived: Boolean(item?.archived),
          updatedAt: String(item?.updatedAt || ''),
          syncedAt: String(item?.syncedAt || '')
        }))
      : []
  }
}

function buildDuplicateQuery() {
  const params = {
    fields: filters.value.fields.join(',')
  }

  const search = filters.value.search.trim()
  if (search) {
    params.search = search
  }

  return params
}

function toggleField(field) {
  if (filters.value.fields.includes(field)) {
    filters.value.fields = filters.value.fields.filter((item) => item !== field)
    return
  }

  filters.value.fields = [...filters.value.fields, field]
}

function resetFilters() {
  filters.value = cloneValue(defaultFilters)
  loadDuplicateGroups()
}

function selectGroup(groupKey) {
  selectedGroupKey.value = groupKey
  selectedCounterpartyIds.value = []
  mergeMessage.value = null
}

function toggleCounterparty(counterpartyId) {
  if (selectedCounterpartyIds.value.includes(counterpartyId)) {
    selectedCounterpartyIds.value = selectedCounterpartyIds.value.filter((id) => id !== counterpartyId)
    return
  }

  selectedCounterpartyIds.value = [...selectedCounterpartyIds.value, counterpartyId]
}

function handleGoToMerge() {
  if (!canGoToMerge.value) {
    return
  }

  mergeMessage.value = null
  router.push({
    name: 'moysklad-merge',
    query: {
      ids: selectedCounterpartyIds.value.join(','),
      ...buildMergeRouteState()
    }
  })
}

function handleSelectAllInGroup() {
  if (!selectedGroupCounterpartyIds.value.length) {
    return
  }

  selectedCounterpartyIds.value = [...selectedGroupCounterpartyIds.value]
  mergeMessage.value = null
}

function clearSelectedCounterparties() {
  selectedCounterpartyIds.value = []
  mergeMessage.value = null
}

function groupTitle(group) {
  return group.values.name || group.values.email || group.values.phone || group.key || 'Группа дублей'
}

function displayValue(value) {
  return value || '—'
}

function buildMergeRouteState() {
  const query = {
    returnFields: filters.value.fields.join(',')
  }

  const search = filters.value.search.trim()
  if (search) {
    query.returnSearch = search
  }

  if (selectedGroupKey.value) {
    query.returnGroup = selectedGroupKey.value
  }

  return query
}

function readFiltersFromRoute(query) {
  const routeFields = parseFieldsQuery(query.fields)
  return {
    fields: routeFields.length ? routeFields : cloneValue(defaultFilters.fields),
    search: normalizeQueryValue(query.search)
  }
}

function parseFieldsQuery(rawValue) {
  const values = Array.isArray(rawValue) ? rawValue : [rawValue]
  const allowed = new Set(fieldOptions.map((item) => item.value))
  const result = []

  for (const value of values) {
    const parts = String(value || '')
      .split(',')
      .map((item) => item.trim())
      .filter(Boolean)

    for (const part of parts) {
      if (!allowed.has(part) || result.includes(part)) {
        continue
      }

      result.push(part)
    }
  }

  return result
}

function resolveInitialGroupKey(items) {
  if (pendingGroupKeyFromRoute && items.some((group) => group.key === pendingGroupKeyFromRoute)) {
    return pendingGroupKeyFromRoute
  }

  return items[0]?.key || null
}

function readMergeMessageFromRoute(query) {
  if (normalizeQueryValue(query.mergeQueued) !== '1') {
    return null
  }

  const jobId = normalizeQueryValue(query.mergeJobId)
  return jobId
    ? `Задача #${jobId} на объединение добавлена в очередь`
    : 'Задача на объединение добавлена в очередь'
}

function normalizeQueryValue(rawValue) {
  if (Array.isArray(rawValue)) {
    return String(rawValue[0] || '').trim()
  }

  return String(rawValue || '').trim()
}

function cloneValue(value) {
  return JSON.parse(JSON.stringify(value))
}
</script>

<template>
  <main class="duplicates-page">
    <header class="duplicates-page__header">
      <div>
        <h1 class="duplicates-page__title">Дубликаты</h1>
        <p class="duplicates-page__subtitle">Поиск, отбор всей группы одним кликом и быстрый переход к объединению</p>
      </div>

      <div class="duplicates-page__header-badges">
        <span class="duplicates-page__badge">Групп: {{ groups.length }}</span>
        <span class="duplicates-page__badge duplicates-page__badge--accent">Выбрано КА: {{ selectedCounterpartyIds.length }}</span>
      </div>
    </header>

    <section class="duplicates-filters">
      <div class="duplicates-filters__section">
        <span class="duplicates-filters__label">Поля поиска дублей</span>
        <div class="duplicates-checkboxes">
          <label v-for="field in fieldOptions" :key="field.value" class="duplicates-checkbox">
            <input
              type="checkbox"
              :checked="filters.fields.includes(field.value)"
              @change="toggleField(field.value)"
            />
            <span>{{ field.label }}</span>
          </label>
        </div>
      </div>

      <label class="duplicates-field duplicates-field--search">
        <span>Строка поиска</span>
        <input
          v-model="filters.search"
          class="duplicates-input"
          type="search"
          placeholder="Поиск по группе или контрагенту"
        />
      </label>

      <div class="duplicates-filters__actions">
        <button class="duplicates-button duplicates-button--primary" type="button" disabled>
          Найти дубли
        </button>
        <button class="duplicates-button duplicates-button--outline" type="button" :disabled="loading" @click="resetFilters">
          Сбросить фильтры
        </button>
      </div>
    </section>

    <p v-if="loading" class="duplicates-message">Загрузка групп дублей...</p>
    <p v-if="error" class="duplicates-error">{{ error }}</p>

    <section class="duplicates-summary">
      <div>
        <span>Найдено групп</span>
        <strong>{{ groups.length }}</strong>
      </div>
      <div>
        <span>Всего контрагентов в группах</span>
        <strong>{{ totalCounterpartiesInGroups }}</strong>
      </div>
      <div>
        <span>Поиск по полям</span>
        <strong>{{ selectedFieldsLabel || '—' }}</strong>
      </div>
    </section>

    <p v-if="groupsTruncated" class="duplicates-warning">
      Показаны не все группы. Увеличьте лимит или уточните поиск.
    </p>

    <section class="duplicates-layout">
      <aside class="duplicates-groups">
        <div v-if="!loading && !groups.length" class="duplicates-empty">
          Группы дублей не найдены
        </div>

        <button
          v-for="group in groups"
          :key="group.key"
          class="duplicates-group"
          :class="{ 'duplicates-group--active': group.key === selectedGroupKey }"
          type="button"
          @click="selectGroup(group.key)"
        >
          <span class="duplicates-group__title">{{ groupTitle(group) }}</span>
          <span class="duplicates-group__meta">Совпадений: {{ group.matchCount }}</span>

          <span v-if="group.values.name" class="duplicates-group__value">Наименование: {{ group.values.name }}</span>
          <span v-if="group.values.email" class="duplicates-group__value">Email: {{ group.values.email }}</span>
          <span v-if="group.values.phone" class="duplicates-group__value">Телефон: {{ group.values.phone }}</span>

          <span class="duplicates-group__footer">
            {{ group.counterparties.length }} контрагентов
            <strong v-if="group.itemsTruncated">Обрезано</strong>
          </span>
        </button>
      </aside>

      <section class="duplicates-details">
        <div class="duplicates-details__header">
          <div>
            <h2>Группа дублей</h2>
            <p>Выберите контрагентов для дальнейшего объединения</p>
          </div>
          <div class="duplicates-details__header-side">
            <div class="duplicates-details__selection">Выбрано: {{ selectedCounterpartyIds.length }}</div>
            <div class="duplicates-details__tools">
              <button
                class="duplicates-button duplicates-button--outline"
                type="button"
                :disabled="!selectedGroupCounterpartyIds.length || allSelectedInGroup"
                @click="handleSelectAllInGroup"
              >
                Выбрать всю группу
              </button>
              <button
                class="duplicates-button duplicates-button--subtle"
                type="button"
                :disabled="!selectedCounterpartyIds.length"
                @click="clearSelectedCounterparties"
              >
                Снять выбор
              </button>
            </div>
          </div>
        </div>

        <div v-if="!selectedGroup" class="duplicates-empty">
          Выберите группу дублей
        </div>

        <div v-else class="duplicates-selection-panel">
          <div class="duplicates-selection-panel__content">
            <strong>{{ groupTitle(selectedGroup) }}</strong>
            <span>
              В группе {{ selectedGroup.counterparties.length }} КА.
              {{ allSelectedInGroup ? 'Сейчас выбраны все дубликаты из этой группы.' : 'Можно выбрать точечно или забрать всю группу одним действием.' }}
            </span>
          </div>
        </div>

        <div v-if="selectedGroup" class="duplicates-table-wrap">
          <table class="duplicates-table">
            <thead>
              <tr>
                <th></th>
                <th>Наименование</th>
                <th>Email</th>
                <th>Телефон</th>
                <th>Архивный</th>
                <th>Обновлён</th>
                <th>Синхронизирован</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="counterparty in selectedGroup.counterparties" :key="counterparty.id">
                <td class="duplicates-table__checkbox-cell">
                  <input
                    type="checkbox"
                    :checked="selectedCounterpartyIds.includes(counterparty.id)"
                    @change="toggleCounterparty(counterparty.id)"
                  />
                </td>
                <td>
                  <strong>{{ displayValue(counterparty.name) }}</strong>
                  <span v-if="counterparty.description">{{ counterparty.description }}</span>
                </td>
                <td>{{ displayValue(counterparty.email) }}</td>
                <td>{{ displayValue(counterparty.phone) }}</td>
                <td>
                  <span class="duplicates-badge" :class="{ 'duplicates-badge--archived': counterparty.archived }">
                    {{ counterparty.archived ? 'Архивный' : 'Активный' }}
                  </span>
                </td>
                <td>{{ displayValue(counterparty.updatedAt) }}</td>
                <td>{{ displayValue(counterparty.syncedAt) }}</td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="duplicates-details__actions">
          <p v-if="mergeMessage" class="duplicates-message">{{ mergeMessage }}</p>
          <button
            class="duplicates-button duplicates-button--primary"
            type="button"
            disabled
          >
            Перейти к объединению
          </button>
        </div>
      </section>
    </section>
  </main>
</template>

<style>
.duplicates-page {
  min-height: 100%;
  padding: 24px 32px;
  background:
    radial-gradient(circle at top left, rgba(45, 108, 223, 0.12), transparent 28%),
    radial-gradient(circle at bottom right, rgba(15, 27, 61, 0.08), transparent 24%),
    #f4f7fb;
}

.duplicates-page__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
  margin-bottom: 22px;
  padding: 24px 26px;
  background: linear-gradient(135deg, #0f1b3d, #214f9d);
  border-radius: 18px;
  box-shadow: 0 18px 36px rgba(15, 35, 80, 0.18);
}

.duplicates-page__title {
  margin: 0;
  color: #ffffff;
  font-size: 28px;
  font-weight: 800;
  line-height: 1.2;
}

.duplicates-page__subtitle {
  margin: 8px 0 0;
  max-width: 640px;
  color: rgba(226, 235, 255, 0.88);
  font-size: 15px;
  line-height: 1.4;
}

.duplicates-page__header-badges {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 10px;
}

.duplicates-page__badge {
  display: inline-flex;
  min-height: 34px;
  align-items: center;
  padding: 0 14px;
  color: #dce8ff;
  font-size: 13px;
  font-weight: 800;
  background: rgba(255, 255, 255, 0.08);
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 999px;
}

.duplicates-page__badge--accent {
  color: #ffffff;
  background: rgba(45, 108, 223, 0.34);
}

.duplicates-filters,
.duplicates-summary,
.duplicates-groups,
.duplicates-details {
  background: #ffffff;
  border: 1px solid #dfe7f3;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(15, 35, 80, 0.06);
}

.duplicates-filters {
  display: grid;
  grid-template-columns: minmax(220px, 1fr) minmax(260px, 1.2fr) minmax(260px, 1fr);
  gap: 18px;
  align-items: end;
  padding: 22px;
}

.duplicates-filters__section,
.duplicates-field {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.duplicates-filters__label,
.duplicates-field span {
  color: #50648f;
  font-size: 13px;
  font-weight: 700;
}

.duplicates-checkboxes,
.duplicates-filters__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.duplicates-field--search {
  min-width: 0;
}

.duplicates-checkbox {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  color: #0f1b3d;
  font-size: 14px;
  font-weight: 600;
  cursor: pointer;
}

.duplicates-checkbox input {
  width: 16px;
  height: 16px;
  accent-color: #2d6cdf;
}

.duplicates-input {
  width: 100%;
  min-height: 40px;
  padding: 9px 12px;
  color: #0f1b3d;
  font-size: 14px;
  background: #ffffff;
  border: 1px solid #cbd8ea;
  border-radius: 8px;
  outline: none;
}

.duplicates-input:focus {
  border-color: #2d6cdf;
  box-shadow: 0 0 0 3px rgba(45, 108, 223, 0.12);
}

.duplicates-button {
  min-height: 40px;
  padding: 9px 16px;
  border: 1px solid transparent;
  border-radius: 8px;
  font-size: 14px;
  font-weight: 700;
  line-height: 1.2;
  cursor: pointer;
  transition:
    background 0.2s ease,
    border-color 0.2s ease,
    color 0.2s ease,
    opacity 0.2s ease;
}

.duplicates-button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.duplicates-button--primary {
  color: #ffffff;
  background: #2d6cdf;
  border-color: #2d6cdf;
}

.duplicates-button--outline {
  color: #2d6cdf;
  background: #ffffff;
  border-color: #9bb8ef;
}

.duplicates-button--subtle {
  color: #50648f;
  background: #f7f9fd;
  border-color: #d9e3f2;
}

.duplicates-summary {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 16px;
  margin-top: 16px;
  padding: 18px 22px;
}

.duplicates-summary div {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.duplicates-summary span {
  color: #64759b;
  font-size: 13px;
}

.duplicates-summary strong {
  overflow: hidden;
  color: #0f1b3d;
  font-size: 18px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.duplicates-message,
.duplicates-error,
.duplicates-warning {
  margin: 12px 0;
  font-size: 14px;
  line-height: 1.4;
}

.duplicates-message {
  color: #1f8f5f;
}

.duplicates-error {
  color: #d92d3f;
}

.duplicates-warning {
  color: #a15c00;
}

.duplicates-layout {
  display: grid;
  grid-template-columns: 360px minmax(0, 1fr);
  gap: 16px;
  margin-top: 16px;
}

.duplicates-groups,
.duplicates-details {
  padding: 16px;
}

.duplicates-groups {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.duplicates-group {
  display: flex;
  flex-direction: column;
  gap: 7px;
  width: 100%;
  padding: 14px;
  text-align: left;
  background: #f8fafd;
  border: 1px solid #dfe7f3;
  border-radius: 10px;
  cursor: pointer;
}

.duplicates-group--active {
  background: #edf4ff;
  border-color: #2d6cdf;
}

.duplicates-group__title {
  color: #0f1b3d;
  font-size: 16px;
  font-weight: 800;
}

.duplicates-group__meta,
.duplicates-group__value,
.duplicates-group__footer {
  color: #64759b;
  font-size: 13px;
  line-height: 1.35;
}

.duplicates-group__footer {
  display: flex;
  justify-content: space-between;
  gap: 10px;
}

.duplicates-group__footer strong {
  color: #a15c00;
}

.duplicates-details {
  min-width: 0;
}

.duplicates-details__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 16px;
}

.duplicates-details__header-side {
  display: grid;
  gap: 12px;
  justify-items: end;
}

.duplicates-details__header h2 {
  margin: 0;
  color: #0f1b3d;
  font-size: 20px;
  font-weight: 800;
}

.duplicates-details__header p {
  margin: 6px 0 0;
  color: #64759b;
  font-size: 14px;
}

.duplicates-details__selection {
  flex: 0 0 auto;
  color: #2d6cdf;
  font-size: 14px;
  font-weight: 800;
}

.duplicates-details__tools {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 10px;
}

.duplicates-selection-panel {
  display: flex;
  align-items: center;
  gap: 14px;
  margin-bottom: 14px;
  padding: 14px 16px;
  background: linear-gradient(180deg, #f8fbff, #eef4ff);
  border: 1px solid #d9e5f8;
  border-radius: 12px;
}

.duplicates-selection-panel__content {
  display: grid;
  gap: 6px;
}

.duplicates-selection-panel__content strong {
  color: #0f1b3d;
  font-size: 15px;
}

.duplicates-selection-panel__content span {
  color: #5d7197;
  font-size: 13px;
  line-height: 1.4;
}

.duplicates-table-wrap {
  overflow-x: auto;
}

.duplicates-table {
  width: 100%;
  min-width: 860px;
  border-collapse: collapse;
}

.duplicates-table th,
.duplicates-table td {
  padding: 11px 10px;
  color: #0f1b3d;
  font-size: 13px;
  text-align: left;
  vertical-align: top;
  border-bottom: 1px solid #dfe7f3;
}

.duplicates-table th {
  color: #50648f;
  font-weight: 800;
  background: #f7f9fd;
}

.duplicates-table__checkbox-cell {
  width: 46px;
}

.duplicates-table td strong,
.duplicates-table td span {
  display: block;
}

.duplicates-table td span {
  margin-top: 4px;
  color: #64759b;
}

.duplicates-badge {
  display: inline-flex;
  align-items: center;
  min-height: 24px;
  padding: 3px 8px;
  color: #1f8f5f;
  font-size: 12px;
  font-weight: 800;
  background: #e7f8f0;
  border-radius: 999px;
}

.duplicates-badge--archived {
  color: #a15c00;
  background: #fff3df;
}

.duplicates-details__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 14px;
  margin-top: 16px;
}

.duplicates-empty {
  padding: 18px;
  color: #64759b;
  font-size: 14px;
  text-align: center;
  background: #f8fafd;
  border: 1px dashed #cbd8ea;
  border-radius: 10px;
}

@media (max-width: 1180px) {
  .duplicates-filters {
    grid-template-columns: 1fr 1fr;
  }

  .duplicates-filters__actions {
    grid-column: 1 / -1;
  }
}

@media (max-width: 900px) {
  .duplicates-page {
    padding: 20px;
  }

  .duplicates-page__header {
    flex-direction: column;
  }

  .duplicates-page__header-badges {
    justify-content: flex-start;
  }

  .duplicates-layout {
    grid-template-columns: 1fr;
  }

  .duplicates-summary {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 640px) {
  .duplicates-filters {
    grid-template-columns: 1fr;
  }

  .duplicates-filters__actions,
  .duplicates-details__actions,
  .duplicates-details__header {
    flex-direction: column;
    align-items: stretch;
  }

  .duplicates-details__header-side,
  .duplicates-details__tools {
    justify-items: stretch;
  }

  .duplicates-button {
    width: 100%;
  }
}
</style>
