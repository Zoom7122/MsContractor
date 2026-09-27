<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Check, Right, Search } from '@element-plus/icons-vue'

import { api } from '../api/http'
import DuplicateGroupItem from '../components/duplicates/DuplicateGroupItem.vue'
import MatchCriteria from '../components/duplicates/MatchCriteria.vue'
import AcceptedNotice from '../components/ui/AcceptedNotice.vue'
import EmptyState from '../components/ui/EmptyState.vue'
import ErrorNotice from '../components/ui/ErrorNotice.vue'
import PageHeader from '../components/ui/PageHeader.vue'
import SectionPanel from '../components/ui/SectionPanel.vue'
import StatusBadge from '../components/ui/StatusBadge.vue'
import { SEARCHABLE_MATCH_FIELDS, groupCriteria, matchFieldLabel } from '../domain/duplicates'
import { toUserError } from '../utils/errors'
import { COUNTERPARTY_FORMS, GROUP_FORMS, countLabel, formatDateTimeShort } from '../utils/format'
import { normalizeQueryValue, parseDelimitedQuery } from '../utils/query'

const props = defineProps({
  duplicatesData: {
    type: Object,
    default: null
  }
})

const fieldOptions = SEARCHABLE_MATCH_FIELDS

const defaultFilters = {
  fields: ['name'],
  search: ''
}

const route = useRoute()
const router = useRouter()
const filters = ref(readFiltersFromRoute(route.query))
const loading = ref(false)
const error = ref(null)
const mergeAccepted = ref(readMergeAcceptedFromRoute(route.query))
const groupsTruncated = ref(false)
const groups = ref([])
const searchedFields = ref([...filters.value.fields])
const selectedGroupKey = ref(null)
const selectedCounterpartyIds = ref([])
let pendingGroupKeyFromRoute = normalizeQueryValue(route.query.group)
let preserveMergeMessageOnNextLoad = Boolean(mergeAccepted.value)

const searchQuery = computed(() => filters.value.search.trim().toLowerCase())

const visibleGroups = computed(() => {
  if (!searchQuery.value) {
    return groups.value
  }

  return groups.value.filter((group) => groupMatchesSearch(group, searchQuery.value))
})

const selectedGroup = computed(() => groups.value.find((group) => group.key === selectedGroupKey.value) || null)
const selectedCriteria = computed(() => groupCriteria(selectedGroup.value))
const matchedFields = computed(() => new Set(selectedCriteria.value.map((item) => item.field)))

const totalCounterpartiesInGroups = computed(() =>
  groups.value.reduce((sum, group) => sum + (group.counterparties?.length || 0), 0)
)

const searchedFieldsLabel = computed(() => searchedFields.value.map(matchFieldLabel).join(', '))

const canGoToMerge = computed(() => selectedCounterpartyIds.value.length >= 2)
const selectedGroupCounterpartyIds = computed(() => selectedGroup.value?.counterparties?.map((item) => item.id) || [])
const allSelectedInGroup = computed(() =>
  selectedGroupCounterpartyIds.value.length > 0 &&
  selectedGroupCounterpartyIds.value.every((id) => selectedCounterpartyIds.value.includes(id))
)
const someSelectedInGroup = computed(() => selectedCounterpartyIds.value.length > 0 && !allSelectedInGroup.value)
const selectedArchivedCount = computed(() =>
  selectedGroup.value?.counterparties.filter((item) => item.archived).length || 0
)
const showSyncedColumn = computed(() => Boolean(selectedGroup.value?.counterparties.some((item) => item.syncedAt)))

const mergeHint = computed(() => {
  if (!selectedCounterpartyIds.value.length) {
    return 'Отметьте контрагентов, которых нужно объединить'
  }
  if (!canGoToMerge.value) {
    return 'Для объединения нужно минимум два контрагента'
  }
  return 'Основного контрагента и итоговые поля выберете на следующем шаге'
})

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

watch(visibleGroups, (items) => {
  if (items.length && !items.some((group) => group.key === selectedGroupKey.value)) {
    selectGroup(items[0].key)
  }
})

async function loadDuplicateGroups() {
  if (!preserveMergeMessageOnNextLoad) {
    mergeAccepted.value = null
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
    searchedFields.value = [...filters.value.fields]
    applyDuplicateResponse(response)
  } catch (requestError) {
    error.value = toUserError(requestError, 'Не удалось загрузить дубликаты')
  } finally {
    loading.value = false
  }
}

function applyDuplicateResponse(response) {
  groups.value = Array.isArray(response)
    ? response.map((group) => normalizeApiGroup(group))
    : Array.isArray(response?.groups) ? response.groups.map(normalizeGroup) : []
  groupsTruncated.value = Boolean(response?.groupsTruncated)

  if (Array.isArray(response?.fields) && response.fields.length) {
    filters.value.fields = response.fields.filter((field) => fieldOptions.some((item) => item.value === field))
    searchedFields.value = [...filters.value.fields]
  }

  selectedCounterpartyIds.value = []
  selectedGroupKey.value = resolveInitialGroupKey(visibleGroups.value.length ? visibleGroups.value : groups.value)
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
    archived: readArchivedFlag(item),
    updatedAt: String(item?.updatedAt || ''),
    syncedAt: ''
  }))
  return {
    key: normalized.map((item) => item.id).sort().join(':'),
    matchCount: normalized.length,
    itemsTruncated: false,
    matchedBy,
    matchValue,
    criteria: normalizeCriteria(group?.criteria),
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
    criteria: normalizeCriteria(group?.criteria),
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
          archived: readArchivedFlag(item),
          updatedAt: String(item?.updatedAt || ''),
          syncedAt: String(item?.syncedAt || '')
        }))
      : []
  }
}

function readArchivedFlag(item) {
  return Boolean(item?.archived ?? item?.Archived)
}

function normalizeCriteria(items) {
  if (!Array.isArray(items)) {
    return []
  }

  return items
    .map((item) => ({ field: String(item?.field || ''), value: String(item?.value || '') }))
    .filter((item) => item.field)
}

function groupMatchesSearch(group, query) {
  const haystack = [
    group.matchValue,
    ...Object.values(group.values || {}),
    ...(group.criteria || []).map((item) => item.value),
    ...group.counterparties.flatMap((item) => [item.name, item.email, item.phone, item.description])
  ]

  return haystack.some((value) => String(value || '').toLowerCase().includes(query))
}

function resetFilters() {
  filters.value = cloneValue(defaultFilters)
  loadDuplicateGroups()
}

function clearSearch() {
  filters.value.search = ''
}

function selectGroup(groupKey) {
  if (groupKey === selectedGroupKey.value) {
    return
  }

  selectedGroupKey.value = groupKey
  selectedCounterpartyIds.value = []
}

function toggleCounterparty(counterpartyId) {
  if (selectedCounterpartyIds.value.includes(counterpartyId)) {
    selectedCounterpartyIds.value = selectedCounterpartyIds.value.filter((id) => id !== counterpartyId)
    return
  }

  selectedCounterpartyIds.value = [...selectedCounterpartyIds.value, counterpartyId]
}

function toggleAllInGroup() {
  if (allSelectedInGroup.value) {
    clearSelectedCounterparties()
    return
  }

  handleSelectAllInGroup()
}

function handleGoToMerge() {
  if (!canGoToMerge.value) {
    return
  }

  mergeAccepted.value = null
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
}

function clearSelectedCounterparties() {
  selectedCounterpartyIds.value = []
}

function groupTitle(group) {
  if (group.matchValue) {
    return group.matchValue
  }
  return group.values.name || group.values.email || group.values.phone || group.criteria?.[0]?.value || 'Группа дублей'
}

function displayValue(value) {
  return value || '—'
}

function rowClassName({ row }) {
  return selectedCounterpartyIds.value.includes(row.id) ? 'duplicates-table__row--selected' : ''
}

function handleRowClick(row, column) {
  if (column?.property === 'selection') {
    return
  }
  toggleCounterparty(row.id)
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
  const routeFields = parseDelimitedQuery(query.fields, fieldOptions.map((item) => item.value))
  return {
    fields: routeFields.length ? routeFields : cloneValue(defaultFilters.fields),
    search: normalizeQueryValue(query.search)
  }
}

function resolveInitialGroupKey(items) {
  if (pendingGroupKeyFromRoute && items.some((group) => group.key === pendingGroupKeyFromRoute)) {
    return pendingGroupKeyFromRoute
  }

  return items[0]?.key || null
}

function readMergeAcceptedFromRoute(query) {
  if (normalizeQueryValue(query.mergeQueued) !== '1') {
    return null
  }

  return { jobId: normalizeQueryValue(query.mergeJobId) }
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
  <div class="app-page duplicates-page">
    <PageHeader
      title="Дубликаты контрагентов"
      subtitle="Найдите совпадающих контрагентов, отметьте нужных и объедините их в одну карточку"
    />

    <AcceptedNotice
      v-if="mergeAccepted"
      title="Объединение принято в обработку"
      description="Задача выполняется в фоне: документы будут перепривязаны, дубликаты — перенесены в архив. Ход выполнения виден в очереди на странице «Обзор»."
      :operation-id="mergeAccepted.jobId"
      closable
      @close="mergeAccepted = null"
    >
      <template #actions>
        <el-button size="small" @click="router.push({ name: 'moysklad-overview' })">Открыть очередь</el-button>
      </template>
    </AcceptedNotice>

    <section class="duplicates-toolbar" aria-label="Параметры поиска">
      <div class="duplicates-toolbar__fields">
        <span class="duplicates-toolbar__label">Совпадение по</span>
        <el-checkbox-group v-model="filters.fields" class="duplicates-toolbar__checks" aria-label="Поля поиска дублей">
          <el-checkbox-button v-for="field in fieldOptions" :key="field.value" :value="field.value">
            <el-icon class="duplicates-toolbar__field-icon" aria-hidden="true"><component :is="field.icon" /></el-icon>
            {{ field.label }}
          </el-checkbox-button>
        </el-checkbox-group>
      </div>

      <el-input
        v-model="filters.search"
        class="duplicates-toolbar__search"
        clearable
        :prefix-icon="Search"
        placeholder="Фильтр по найденным: имя, email, телефон"
        aria-label="Фильтр по найденным группам"
      />

      <div class="duplicates-toolbar__actions">
        <el-button
          type="primary"
          :loading="loading"
          :disabled="loading || !filters.fields.length"
          @click="loadDuplicateGroups"
        >
          Найти дубли
        </el-button>
        <el-button text :disabled="loading" @click="resetFilters">Сбросить</el-button>
      </div>
    </section>

    <ErrorNotice
      v-if="error"
      :error="error"
      retryable
      :retrying="loading"
      @retry="loadDuplicateGroups"
    />

    <el-alert
      v-if="groupsTruncated"
      title="Показаны не все группы — уточните поиск или увеличьте лимит в настройках."
      type="warning"
      :closable="false"
      show-icon
    />

    <p v-if="!loading && groups.length" class="duplicates-summary">
      Найдено <strong>{{ countLabel(groups.length, GROUP_FORMS) }}</strong>,
      в них <strong>{{ countLabel(totalCounterpartiesInGroups, COUNTERPARTY_FORMS) }}</strong>
      <span class="app-text-muted"> · совпадение по: {{ searchedFieldsLabel }}</span>
      <template v-if="searchQuery">
        <span class="app-text-muted"> · после фильтра: {{ visibleGroups.length }}</span>
      </template>
    </p>

    <!-- Loading skeleton keeps the final layout to avoid shifts -->
    <section v-if="loading" class="duplicates-layout" aria-busy="true">
      <SectionPanel class="duplicates-groups" title="Группы" flush>
        <div class="duplicates-skeleton-list">
          <el-skeleton v-for="index in 6" :key="index" animated>
            <template #template>
              <el-skeleton-item variant="text" style="width: 70%" />
              <el-skeleton-item variant="text" style="width: 45%; margin-top: 8px" />
            </template>
          </el-skeleton>
        </div>
      </SectionPanel>
      <SectionPanel class="duplicates-details" title="Контрагенты группы">
        <el-skeleton :rows="5" animated />
      </SectionPanel>
    </section>

    <SectionPanel v-else-if="!groups.length && !error" class="duplicates-empty">
      <EmptyState
        image="success"
        title="Дубликаты не найдены"
        :description="`Совпадений по полям «${searchedFieldsLabel || '—'}» нет. Попробуйте добавить другие поля или обновите данные синхронизацией на странице «Обзор».`"
      />
    </SectionPanel>

    <section v-else-if="groups.length" class="duplicates-layout">
      <SectionPanel class="duplicates-groups" flush>
        <template #title>
          <h2 class="duplicates-groups__title">
            Группы <span class="app-meta app-nums">{{ visibleGroups.length }}</span>
          </h2>
        </template>

        <EmptyState
          v-if="!visibleGroups.length"
          size="sm"
          image="search"
          title="Ничего не найдено"
          :description="`Нет групп, где встречается «${filters.search.trim()}».`"
        >
          <el-button size="small" @click="clearSearch">Сбросить фильтр</el-button>
        </EmptyState>

        <ul v-else class="duplicates-groups__list">
          <li v-for="group in visibleGroups" :key="group.key">
            <DuplicateGroupItem
              :group="group"
              :title="groupTitle(group)"
              :active="group.key === selectedGroupKey"
              @select="selectGroup"
            />
          </li>
        </ul>
      </SectionPanel>

      <SectionPanel v-if="selectedGroup" class="duplicates-details" flush>
        <template #title>
          <span class="app-meta">Группа дублей</span>
          <h2 class="duplicates-details__title">{{ groupTitle(selectedGroup) }}</h2>
        </template>
        <template #actions>
          <span class="app-meta">
            {{ countLabel(selectedGroup.counterparties.length, COUNTERPARTY_FORMS) }}<template v-if="selectedArchivedCount">, архивных: {{ selectedArchivedCount }}</template>
          </span>
        </template>

        <div class="duplicates-details__criteria">
          <span class="duplicates-details__criteria-label">Совпадает</span>
          <MatchCriteria :criteria="selectedCriteria" show-values />
        </div>

        <el-table
          class="duplicates-table"
          :data="selectedGroup.counterparties"
          :row-class-name="rowClassName"
          row-key="id"
          table-layout="fixed"
          empty-text="В группе нет контрагентов"
          @row-click="handleRowClick"
        >
          <el-table-column width="44" align="center" property="selection">
            <template #header>
              <el-checkbox
                :model-value="allSelectedInGroup"
                :indeterminate="someSelectedInGroup"
                aria-label="Выбрать всех в группе"
                @change="toggleAllInGroup"
              />
            </template>
            <template #default="{ row }">
              <el-checkbox
                :model-value="selectedCounterpartyIds.includes(row.id)"
                :aria-label="`Выбрать ${displayValue(row.name)}`"
                @click.stop
                @change="toggleCounterparty(row.id)"
              />
            </template>
          </el-table-column>

          <el-table-column label="Контрагент" min-width="220">
            <template #default="{ row }">
              <div class="duplicates-table__name">
                <span class="app-clamp-2">
                  <span :class="{ 'duplicates-table__match': matchedFields.has('name') && row.name }">{{ displayValue(row.name) }}</span>
                </span>
                <el-tooltip v-if="row.description" :content="row.description" placement="top" :show-after="400">
                  <span class="duplicates-table__description app-truncate">{{ row.description }}</span>
                </el-tooltip>
              </div>
            </template>
          </el-table-column>

          <el-table-column label="Email" min-width="180">
            <template #default="{ row }">
              <span class="duplicates-table__value" :class="{ 'duplicates-table__match': matchedFields.has('email') && row.email }">
                {{ displayValue(row.email) }}
              </span>
            </template>
          </el-table-column>

          <el-table-column label="Телефон" min-width="150">
            <template #default="{ row }">
              <span class="duplicates-table__value app-nums" :class="{ 'duplicates-table__match': matchedFields.has('phone') && row.phone }">
                {{ displayValue(row.phone) }}
              </span>
            </template>
          </el-table-column>

          <el-table-column label="Статус" width="112">
            <template #default="{ row }">
              <StatusBadge size="sm" :tone="row.archived ? 'neutral' : 'success'" :label="row.archived ? 'Архивный' : 'Активный'" />
            </template>
          </el-table-column>

          <el-table-column label="Обновлён" width="140">
            <template #default="{ row }">
              <span class="app-text-secondary app-nums">{{ formatDateTimeShort(row.updatedAt) }}</span>
            </template>
          </el-table-column>

          <el-table-column v-if="showSyncedColumn" label="Синхронизирован" width="140">
            <template #default="{ row }">
              <span class="app-text-secondary app-nums">{{ formatDateTimeShort(row.syncedAt) }}</span>
            </template>
          </el-table-column>
        </el-table>

        <div class="duplicates-actionbar">
          <div class="duplicates-actionbar__selection">
            <strong class="app-nums">Выбрано {{ selectedCounterpartyIds.length }} из {{ selectedGroup.counterparties.length }}</strong>
            <span class="app-meta">{{ mergeHint }}</span>
          </div>
          <div class="duplicates-actionbar__buttons">
            <el-button
              text
              :icon="Check"
              :disabled="!selectedGroupCounterpartyIds.length || allSelectedInGroup"
              @click="handleSelectAllInGroup"
            >
              Выбрать всех
            </el-button>
            <el-button text :disabled="!selectedCounterpartyIds.length" @click="clearSelectedCounterparties">
              Снять выбор
            </el-button>
            <el-button type="primary" :disabled="!canGoToMerge" @click="handleGoToMerge">
              Объединить выбранных
              <el-icon class="el-icon--right"><Right /></el-icon>
            </el-button>
          </div>
        </div>
      </SectionPanel>

      <SectionPanel v-else class="duplicates-details">
        <EmptyState image="select" title="Выберите группу" description="Слева — найденные группы дублей. Откройте любую, чтобы увидеть контрагентов." />
      </SectionPanel>
    </section>
  </div>
</template>

<style scoped src="../styles/pages/duplicates.css"></style>
