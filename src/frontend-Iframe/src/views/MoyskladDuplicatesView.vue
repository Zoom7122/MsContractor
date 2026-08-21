<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '../api/http'

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

async function loadDuplicateGroups() {
  if (!preserveMergeMessageOnNextLoad) {
    mergeMessage.value = null
  }
  preserveMergeMessageOnNextLoad = false

  if (!filters.value.fields.length) {
    error.value = 'Выберите хотя бы одно поле поиска'
    return
  }

  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams()
    filters.value.fields.forEach((field) => params.append('fields', field))
    const response = await api.post('/api/merge-preview', null, {
      params
    })
    applyDuplicateResponse(response)
  } catch (requestError) {
    error.value = requestError?.message || 'Не удалось загрузить дубликаты'
  } finally {
    loading.value = false
  }
}

function applyDuplicateResponse(response) {
  groups.value = Array.isArray(response)
    ? response.map((group) => normalizeApiGroup(group))
    : Array.isArray(response?.groups) ? response.groups.map(normalizeGroup) : []
  groupsTruncated.value = false

  if (Array.isArray(response?.fields) && response.fields.length) {
    filters.value.fields = response.fields.filter((field) => fieldOptions.some((item) => item.value === field))
  }

  selectedCounterpartyIds.value = []
  selectedGroupKey.value = resolveInitialGroupKey(groups.value)
  pendingGroupKeyFromRoute = ''
}

function normalizeApiGroup(group) {
  const items = Array.isArray(group?.counterparties) ? group.counterparties : []
  const matchedBy = String(group?.matchedBy || '')
  const matchValue = String(group?.matchValue || '')
  const normalized = items.map((item) => ({
    id: String(item?.id || ''),
    name: String(item?.name || ''),
    description: String(item?.description || ''),
    email: String(item?.email || ''),
    phone: String(item?.phone || ''),
    archived: false,
    updatedAt: String(item?.updatedAt || ''),
    syncedAt: ''
  }))
  return {
    key: normalized.map((item) => item.id).sort().join(':'),
    matchCount: normalized.length,
    itemsTruncated: false,
    matchedBy,
    matchValue,
    values: {
      name: matchedBy === 'name' ? matchValue : '',
      email: matchedBy === 'email' ? matchValue : '',
      phone: matchedBy === 'phone' ? matchValue : ''
    },
    counterparties: normalized
  }
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
  if (group.matchValue) {
    const field = fieldOptions.find((item) => item.value === group.matchedBy)
    return `Дубликаты по ${field?.label || group.matchedBy}: ${group.matchValue}`
  }
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

onMounted(() => {
  if (!props.duplicatesData) {
    loadDuplicateGroups()
  }
})
</script>

<template>
  <main class="duplicates-page">
    <header class="duplicates-page__header">
      <div>
        <h1 class="duplicates-page__title">Дубликаты</h1>
        <p class="duplicates-page__subtitle">Поиск, отбор всей группы одним кликом и быстрый переход к объединению</p>
      </div>

      <div class="duplicates-page__header-badges">
        <el-tag type="info" effect="plain">Групп: {{ groups.length }}</el-tag>
        <el-tag type="primary" effect="light">Выбрано КА: {{ selectedCounterpartyIds.length }}</el-tag>
      </div>
    </header>

    <el-card class="duplicates-filters" shadow="never">
      <el-form class="duplicates-filters__form" label-position="top" size="small">
        <el-form-item label="Поля поиска дублей">
          <div class="duplicates-checkboxes">
            <el-checkbox
              v-for="field in fieldOptions"
              :key="field.value"
              :model-value="filters.fields.includes(field.value)"
              @change="toggleField(field.value)"
            >
              {{ field.label }}
            </el-checkbox>
          </div>
        </el-form-item>

        <el-form-item class="duplicates-field--search" label="Строка поиска">
          <el-input
            v-model="filters.search"
            clearable
            placeholder="Поиск по группе или контрагенту"
            @keydown.enter.prevent="loadDuplicateGroups"
          />
        </el-form-item>

        <div class="duplicates-filters__actions">
          <el-button
            type="primary"
            :loading="loading"
            :disabled="loading || !filters.fields.length"
            @click="loadDuplicateGroups"
          >
            Найти дубли
          </el-button>
          <el-button plain :disabled="loading" @click="resetFilters">Сбросить фильтры</el-button>
        </div>
      </el-form>
    </el-card>

    <el-skeleton v-if="loading" :rows="3" animated />
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />

    <section class="duplicates-summary">
      <el-card shadow="never">
        <span>Найдено групп</span>
        <strong>{{ groups.length }}</strong>
      </el-card>
      <el-card shadow="never">
        <span>Всего контрагентов в группах</span>
        <strong>{{ totalCounterpartiesInGroups }}</strong>
      </el-card>
      <el-card shadow="never">
        <span>Поиск по полям</span>
        <strong>{{ selectedFieldsLabel || '—' }}</strong>
      </el-card>
    </section>

    <el-alert
      v-if="groupsTruncated"
      title="Показаны не все группы. Увеличьте лимит или уточните поиск."
      type="warning"
      :closable="false"
      show-icon
    />

    <section class="duplicates-layout">
      <aside class="duplicates-groups">
        <el-empty v-if="!loading && !groups.length" description="Группы дублей не найдены" :image-size="64" />

        <el-card
          v-for="group in groups"
          :key="group.key"
          class="duplicates-group"
          :class="{ 'duplicates-group--active': group.key === selectedGroupKey }"
          shadow="never"
          role="button"
          tabindex="0"
          @click="selectGroup(group.key)"
          @keydown.enter.prevent="selectGroup(group.key)"
        >
          <span class="duplicates-group__title">{{ groupTitle(group) }}</span>
          <span class="duplicates-group__meta">Совпадений: {{ group.matchCount }}</span>
          <span v-if="group.values.name" class="duplicates-group__value">Наименование: {{ group.values.name }}</span>
          <span v-if="group.values.email" class="duplicates-group__value">Email: {{ group.values.email }}</span>
          <span v-if="group.values.phone" class="duplicates-group__value">Телефон: {{ group.values.phone }}</span>
          <span class="duplicates-group__footer">
            {{ group.counterparties.length }} контрагентов
            <el-tag v-if="group.itemsTruncated" type="warning" size="small">Обрезано</el-tag>
          </span>
        </el-card>
      </aside>

      <el-card class="duplicates-details" shadow="never">
        <div class="duplicates-details__header">
          <div>
            <h2>Группа дублей</h2>
            <p>Выберите контрагентов для дальнейшего объединения</p>
          </div>
          <div class="duplicates-details__header-side">
            <el-tag type="info" effect="plain">Выбрано: {{ selectedCounterpartyIds.length }}</el-tag>
            <div class="duplicates-details__tools">
              <el-button
                plain
                size="small"
                :disabled="!selectedGroupCounterpartyIds.length || allSelectedInGroup"
                @click="handleSelectAllInGroup"
              >
                Выбрать всю группу
              </el-button>
              <el-button
                text
                size="small"
                :disabled="!selectedCounterpartyIds.length"
                @click="clearSelectedCounterparties"
              >
                Снять выбор
              </el-button>
            </div>
          </div>
        </div>

        <el-empty v-if="!selectedGroup" description="Выберите группу дублей" :image-size="72" />

        <el-alert
          v-else
          class="duplicates-selection-panel"
          :title="groupTitle(selectedGroup)"
          :description="`В группе ${selectedGroup.counterparties.length} КА. ${allSelectedInGroup ? 'Сейчас выбраны все дубликаты из этой группы.' : 'Можно выбрать точечно или забрать всю группу одним действием.'}`"
          type="info"
          :closable="false"
          show-icon
        />

        <el-table
          v-if="selectedGroup"
          class="duplicates-table"
          :data="selectedGroup.counterparties"
          border
          table-layout="auto"
          empty-text="В группе нет контрагентов"
        >
          <el-table-column width="46" align="center">
            <template #default="{ row }">
              <el-checkbox
                :model-value="selectedCounterpartyIds.includes(row.id)"
                :aria-label="`Выбрать ${displayValue(row.name)}`"
                @change="toggleCounterparty(row.id)"
              />
            </template>
          </el-table-column>
          <el-table-column label="Наименование" min-width="180">
            <template #default="{ row }">
              <div class="duplicates-table__name">
                <strong>{{ displayValue(row.name) }}</strong>
                <span v-if="row.description">{{ row.description }}</span>
              </div>
            </template>
          </el-table-column>
          <el-table-column label="Email" min-width="150">
            <template #default="{ row }">{{ displayValue(row.email) }}</template>
          </el-table-column>
          <el-table-column label="Телефон" min-width="130">
            <template #default="{ row }">{{ displayValue(row.phone) }}</template>
          </el-table-column>
          <el-table-column label="Статус" width="100">
            <template #default="{ row }">
              <el-tag :type="row.archived ? 'info' : 'success'" size="small" effect="light">
                {{ row.archived ? 'Архивный' : 'Активный' }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column label="Обновлён" min-width="140">
            <template #default="{ row }">{{ displayValue(row.updatedAt) }}</template>
          </el-table-column>
          <el-table-column label="Синхронизирован" min-width="140">
            <template #default="{ row }">{{ displayValue(row.syncedAt) }}</template>
          </el-table-column>
        </el-table>

        <div class="duplicates-details__actions">
          <el-alert v-if="mergeMessage" :title="mergeMessage" type="success" :closable="false" show-icon />
          <el-button type="primary" :disabled="!canGoToMerge" @click="handleGoToMerge">
            Перейти к объединению
          </el-button>
        </div>
      </el-card>
    </section>
  </main>
</template>

<style scoped src="../styles/pages/duplicates.css"></style>
