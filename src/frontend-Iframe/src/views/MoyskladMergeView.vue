<script setup>
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import { Box, Check, CircleCheckFilled, Message, Phone, Right, WarningFilled } from '@element-plus/icons-vue'

import { api } from '../api/http'
import MergeDocumentRules from '../components/merge/MergeDocumentRules.vue'
import MergeDocumentsTable from '../components/merge/MergeDocumentsTable.vue'
import EmptyState from '../components/ui/EmptyState.vue'
import ErrorNotice from '../components/ui/ErrorNotice.vue'
import PageHeader from '../components/ui/PageHeader.vue'
import SectionPanel from '../components/ui/SectionPanel.vue'
import StatusBadge from '../components/ui/StatusBadge.vue'
import { normalizeMergeDocument } from '../domain/merge'
import { toUserError } from '../utils/errors'
import { COUNTERPARTY_FORMS, DOCUMENT_FORMS, DUPLICATE_FORMS, countLabel, formatDateTimeShort } from '../utils/format'
import { normalizeQueryValue, parseDelimitedQuery } from '../utils/query'

const props = defineProps({
  /**
   * Documents of the selected counterparties, when a source for them exists
   * (mock / future endpoint). `null` — the list is built during the merge job.
   */
  documentsPreview: {
    type: Array,
    default: null
  }
})

const route = useRoute()
const router = useRouter()

const loading = ref(false)
const previewLoading = ref(false)
const submitLoading = ref(false)
const error = ref(null)
const submitError = ref(null)
const counterparties = ref([])
const primaryCounterpartyId = ref('')
const fieldSelections = ref(createEmptyFieldSelections())
const attributeSelections = ref({})

let previewRequestToken = 0

const primaryCounterparty = computed(() => {
  return counterparties.value.find((counterparty) => counterparty.id === primaryCounterpartyId.value) || null
})

const duplicateCounterparties = computed(() =>
  counterparties.value.filter((counterparty) => counterparty.id !== primaryCounterpartyId.value)
)

const selectedCounterpartyIds = computed(() => parseDelimitedQuery(route.query.ids))

const fieldRows = computed(() => [
  { key: 'name', label: 'Наименование', required: true },
  { key: 'description', label: 'Описание', type: 'text' },
  { key: 'email', label: 'Email' },
  { key: 'phone', label: 'Телефон' }
])

const attributeRows = computed(() => {
  const attributesById = new Map()
  for (const counterparty of counterparties.value) {
    for (const attribute of counterparty.attributes) {
      if (!attribute.id) {
        continue
      }
      const current = attributesById.get(attribute.id)
      if (!current) {
        attributesById.set(attribute.id, {
          id: attribute.id,
          name: attribute.name,
          type: attribute.type
        })
      } else {
        current.name ||= attribute.name
        current.type ||= attribute.type
      }
    }
  }
  return [...attributesById.values()].sort((left, right) => (left.name || left.id).localeCompare(right.name || right.id, 'ru'))
})

const steps = [
  { id: 'merge-step-primary', label: 'Основной контрагент' },
  { id: 'merge-step-fields', label: 'Итоговые поля' },
  { id: 'merge-step-documents', label: 'Документы' },
  { id: 'merge-step-archive', label: 'Архивация' },
  { id: 'merge-step-result', label: 'Результат' }
]

const canSubmit = computed(() => {
  return !submitLoading.value &&
    counterparties.value.length >= 2 &&
    Boolean(primaryCounterpartyId.value) &&
    !primaryCounterparty.value?.archived &&
    Boolean(fieldSelections.value.name.value.trim())
})

const headerStatus = computed(() => {
  if (submitLoading.value) {
    return { label: 'Отправляем в очередь', tone: 'primary' }
  }
  if (previewLoading.value && !counterparties.value.length) {
    return { label: 'Готовим предпросмотр', tone: 'neutral' }
  }
  if (canSubmit.value) {
    return { label: 'Готово к запуску', tone: 'success', icon: CircleCheckFilled }
  }
  if (counterparties.value.length) {
    return { label: 'Требует внимания', tone: 'warning', icon: WarningFilled }
  }
  return null
})

/** Fields whose final value differs from what the primary counterparty has now. */
const changedFields = computed(() => {
  const primary = primaryCounterparty.value
  if (!primary) {
    return []
  }

  const standardChanges = fieldRows.value
    .filter((field) => (fieldSelections.value[field.key].value || '') !== (primary[field.key] || ''))
    .map((field) => ({
      ...field,
      before: primary[field.key] || '',
      after: fieldSelections.value[field.key].value || ''
    }))

  const attributeChanges = attributeRows.value
    .filter(attribute => isAttributeChanged(attribute))
    .map(attribute => {
      const current = attributeValue(primary, attribute)
      const selection = attributeSelections.value[attribute.id]
      return {
        key: `attribute-${attribute.id}`,
        label: attribute.name || `Дополнительное поле ${attribute.id}`,
        before: formatAttributeValue(attribute, current, attributeValueJson(primary, attribute)),
        after: selection.clear
          ? 'пусто'
          : formatAttributeValue(attribute, selectedAttributeValue(attribute, selection), selectedAttributeValueJson(attribute, selection))
      }
    })

  return [...standardChanges, ...attributeChanges]
})

/** Non-empty duplicate values that will not reach the final card. */
const droppedValues = computed(() => {
  const result = []
  for (const field of fieldRows.value) {
    const finalValue = fieldSelections.value[field.key].value || ''
    const seen = new Set()
    for (const counterparty of counterparties.value) {
      const value = counterparty[field.key] || ''
      if (!value || value === finalValue || seen.has(value)) {
        continue
      }
      seen.add(value)
      result.push({ field: field.label, value, source: counterparty.name || 'Контрагент' })
    }
  }

  for (const attribute of attributeRows.value) {
    const selection = attributeSelections.value[attribute.id]
    const finalKey = selection?.clear ? 'null' : selectedAttributeValueJson(attribute, selection)
    const seen = new Set()
    for (const counterparty of duplicateCounterparties.value) {
      const value = attributeValue(counterparty, attribute)
      const valueKey = attributeValueJson(counterparty, attribute)
      if (isEmptyAttributeValue(value) || valueKey === finalKey || seen.has(valueKey)) {
        continue
      }
      seen.add(valueKey)
      result.push({
        field: attribute.name || 'Дополнительное поле',
        value: formatAttributeValue(attribute, value, valueKey),
        source: counterparty.name || 'Контрагент'
      })
    }
  }
  return result
})

const documents = computed(() => {
  if (!Array.isArray(props.documentsPreview)) {
    return null
  }

  const duplicateIds = new Set(duplicateCounterparties.value.map((item) => item.id))
  return props.documentsPreview
    .map(normalizeMergeDocument)
    .filter((document) => duplicateIds.has(document.counterpartyId))
})

const documentCounts = computed(() => {
  const list = documents.value || []
  return {
    total: list.length,
    reassign: list.filter((item) => item.action === 'reassign').length,
    recreate: list.filter((item) => item.action === 'recreate').length
  }
})

const problems = computed(() => {
  const list = []
  if (counterparties.value.length && !fieldSelections.value.name.value.trim()) {
    list.push({ tone: 'danger', text: 'Наименование итоговой карточки не может быть пустым — выберите значение.' })
  }
  if (primaryCounterparty.value?.archived) {
    list.push({ tone: 'danger', text: 'Архивный контрагент не может быть основным.' })
  }
  return list
})

watch(primaryCounterpartyId, (nextId) => {
  if (!nextId) {
    return
  }

  for (const fieldKey of Object.keys(fieldSelections.value)) {
    const currentSelection = fieldSelections.value[fieldKey]
    if (!currentSelection.sourceCounterpartyId || !counterpartyExists(currentSelection.sourceCounterpartyId)) {
      applyFieldSelection(fieldKey, nextId)
    }
  }

  for (const attribute of attributeRows.value) {
    const currentSelection = attributeSelections.value[attribute.id]
    if (!currentSelection?.sourceCounterpartyId || !counterpartyExists(currentSelection.sourceCounterpartyId)) {
      applyAttributeSelection(attribute, nextId)
    }
  }
})

watch(
  () => selectedCounterpartyIds.value.join(','),
  () => {
    loadPreview()
  },
  { immediate: true }
)

async function loadPreview() {
  const ids = selectedCounterpartyIds.value
  const requestToken = ++previewRequestToken

  if (ids.length < 2) {
    error.value = null
    counterparties.value = []
    primaryCounterpartyId.value = ''
    fieldSelections.value = createEmptyFieldSelections()
    attributeSelections.value = {}
    return
  }

  loading.value = true
  previewLoading.value = true
  error.value = null
  submitError.value = null

  try {
    const response = await api.post('/api/merge-preview/selection', {
      counterpartyIds: ids
    })
    if (requestToken !== previewRequestToken) {
      return
    }

    counterparties.value = normalizeCounterparties(response?.counterparties)
    primaryCounterpartyId.value = counterparties.value.find((item) => !item.archived)?.id || ''
    initializeFieldSelections()
    initializeAttributeSelections()
  } catch (requestError) {
    if (requestToken !== previewRequestToken) {
      return
    }
    error.value = toUserError(requestError, 'Не удалось загрузить предпросмотр объединения')
    counterparties.value = []
    primaryCounterpartyId.value = ''
    fieldSelections.value = createEmptyFieldSelections()
    attributeSelections.value = {}
  } finally {
    if (requestToken === previewRequestToken) {
      loading.value = false
      previewLoading.value = false
    }
  }
}

function initializeFieldSelections() {
  const primaryId = primaryCounterpartyId.value
  const nextSelections = createEmptyFieldSelections()

  for (const fieldKey of Object.keys(nextSelections)) {
    if (primaryId) {
      nextSelections[fieldKey] = buildFieldSelection(fieldKey, primaryId)
    }
  }

  fieldSelections.value = nextSelections
}

function initializeAttributeSelections() {
  const primaryId = primaryCounterpartyId.value
  const nextSelections = {}
  for (const attribute of attributeRows.value) {
    nextSelections[attribute.id] = buildAttributeSelection(attribute, primaryId)
  }
  attributeSelections.value = nextSelections
}

function handlePrimaryChange(counterpartyId) {
  const counterparty = counterparties.value.find((item) => item.id === counterpartyId)
  if (!counterparty || counterparty.archived) {
    return
  }
  primaryCounterpartyId.value = counterpartyId
}

function handleFieldSelection(fieldKey, counterpartyId) {
  applyFieldSelection(fieldKey, counterpartyId)
}

function applyFieldSelection(fieldKey, counterpartyId) {
  const counterparty = counterparties.value.find((item) => item.id === counterpartyId)
  if (!counterparty || !fieldSelections.value[fieldKey]) {
    return
  }

  fieldSelections.value[fieldKey] = buildFieldSelection(fieldKey, counterpartyId, counterparty)
}

function buildFieldSelection(fieldKey, counterpartyId, counterparty = null) {
  const source = counterparty || counterparties.value.find((item) => item.id === counterpartyId)
  return {
    sourceCounterpartyId: counterpartyId,
    value: String(source?.[fieldKey] || '')
  }
}

function counterpartyExists(counterpartyId) {
  return counterparties.value.some((item) => item.id === counterpartyId)
}

/** Distinct values of a field across the selected counterparties, empty value last. */
function fieldOptions(fieldKey) {
  const options = []
  for (const counterparty of counterparties.value) {
    const value = String(counterparty[fieldKey] || '')
    const existing = options.find((option) => option.value === value)
    if (existing) {
      existing.sources.push(counterparty)
    } else {
      options.push({ value, sources: [counterparty] })
    }
  }
  return options.sort((left, right) => Number(left.value === '') - Number(right.value === ''))
}

function attributeOptions(attribute) {
  const optionsByValue = new Map()
  for (const counterparty of counterparties.value) {
    const value = attributeValue(counterparty, attribute)
    const valueJson = attributeValueJson(counterparty, attribute)
    const key = valueJson
    const current = optionsByValue.get(key)
    if (current) {
      current.sources.push(counterparty)
    } else {
      optionsByValue.set(key, { key, value, valueJson, sources: [counterparty], clear: false })
    }
  }

  const options = [...optionsByValue.values()]
    .sort((left, right) => Number(isEmptyAttributeValue(left.value)) - Number(isEmptyAttributeValue(right.value)))
  if (options.some(option => option.value !== null && option.value !== undefined)) {
    options.push({ key: '__clear__', value: null, sources: [], clear: true })
  }
  return options
}

function isOptionSelected(fieldKey, option) {
  return (fieldSelections.value[fieldKey].value || '') === option.value
}

function selectOption(fieldKey, option) {
  const source = option.sources.find((item) => item.id === primaryCounterpartyId.value) || option.sources[0]
  handleFieldSelection(fieldKey, source.id)
}

function attributeValue(counterparty, attribute) {
  const source = counterparty?.attributes?.find(item => item.id === attribute.id)
  return source ? (attribute.type === 'file' ? source.file ?? null : source.value ?? null) : null
}

function attributeValueJson(counterparty, attribute) {
  const source = counterparty?.attributes?.find(item => item.id === attribute.id)
  return source ? (attribute.type === 'file' ? source.fileJson : source.valueJson) || 'null' : 'null'
}

function selectedAttributeValue(attribute, selection) {
  if (!selection || selection.clear) {
    return null
  }
  return attribute.type === 'file' ? selection.file ?? null : selection.value ?? null
}

function selectedAttributeValueJson(attribute, selection) {
  if (!selection || selection.clear) {
    return 'null'
  }
  return attribute.type === 'file' ? selection.fileJson : selection.valueJson
}

function buildAttributeSelection(attribute, counterpartyId) {
  const source = counterparties.value.find(item => item.id === counterpartyId)
  const rawAttribute = source?.attributes?.find(item => item.id === attribute.id)
  return {
    sourceCounterpartyId: counterpartyId,
    value: rawAttribute?.value ?? null,
    file: rawAttribute?.file ?? null,
    valueJson: rawAttribute?.valueJson ?? 'null',
    fileJson: rawAttribute?.fileJson ?? 'null',
    clear: false
  }
}

function applyAttributeSelection(attribute, counterpartyId) {
  const source = counterparties.value.find(item => item.id === counterpartyId)
  if (!source) {
    return
  }
  attributeSelections.value[attribute.id] = buildAttributeSelection(attribute, counterpartyId)
}

function selectAttributeOption(attribute, option) {
  if (option.clear) {
    attributeSelections.value[attribute.id] = {
      sourceCounterpartyId: primaryCounterpartyId.value,
      value: null,
      file: null,
      valueJson: 'null',
      fileJson: 'null',
      clear: true
    }
    return
  }

  const source = option.sources.find(item => item.id === primaryCounterpartyId.value) || option.sources[0]
  if (source) {
    applyAttributeSelection(attribute, source.id)
  }
}

function isAttributeOptionSelected(attribute, option) {
  const selection = attributeSelections.value[attribute.id]
  if (option.clear) {
    return Boolean(selection?.clear)
  }
  return Boolean(selection) && !selection.clear &&
    selectedAttributeValueJson(attribute, selection) === option.valueJson
}

function isAttributeChanged(attribute) {
  const primary = primaryCounterparty.value
  const selection = attributeSelections.value[attribute.id]
  if (!primary || !selection) {
    return false
  }
  return attributeValueJson(primary, attribute) !== selectedAttributeValueJson(attribute, selection)
}

function isEmptyAttributeValue(value) {
  return value === null || value === undefined || value === ''
}

function formatAttributeValue(attribute, value, valueJson = null) {
  if (value === null || value === undefined) {
    return 'Не задано'
  }
  if (value === '') {
    return 'Пустая строка'
  }
  if (['long', 'double'].includes(attribute.type) && valueJson) {
    return valueJson
  }
  if (typeof value === 'boolean') {
    return value ? 'Да' : 'Нет'
  }
  if (attribute.type === 'file' && value && typeof value === 'object') {
    return String(value.filename || 'Файл')
  }
  if (value && typeof value === 'object') {
    return String(value.filename || value.name || value.meta?.href || JSON.stringify(value))
  }
  return String(value)
}

function optionSourcesLabel(option) {
  if (option.sources.length === counterparties.value.length && option.sources.length > 1) {
    return 'у всех'
  }
  return option.sources.map((item) => (item.id === primaryCounterpartyId.value ? 'основной' : item.name || 'контрагент')).join(', ')
}

function isFieldChanged(fieldKey) {
  return changedFields.value.some((field) => field.key === fieldKey)
}

function buildMergeJobRequest() {
  const primaryId = primaryCounterpartyId.value
  const duplicateCounterpartyIds = counterparties.value
    .map((counterparty) => counterparty.id)
    .filter((id) => id !== primaryId)

  return {
    mainCounterpartyId: primaryId,
    duplicateCounterpartyIds,
    mainCounterparty: {
      name: fieldSelections.value.name.value || '',
      description: fieldSelections.value.description.value || '',
      email: fieldSelections.value.email.value || '',
      phone: fieldSelections.value.phone.value || '',
      attributes: attributeRows.value
        .filter(attribute => isAttributeChanged(attribute))
        .map(attribute => {
          const selection = attributeSelections.value[attribute.id]
          return {
            id: attribute.id,
            type: attribute.type,
            sourceCounterpartyId: selection.sourceCounterpartyId,
            value: selection.value,
            file: selection.file,
            valueJson: selection.valueJson,
            fileJson: selection.fileJson,
            clear: Boolean(selection.clear)
          }
        })
    }
  }
}

async function confirmAndSubmit() {
  if (!canSubmit.value) {
    return
  }

  const duplicatesCount = duplicateCounterparties.value.length
  try {
    await ElMessageBox.confirm(
      `${countLabel(duplicatesCount, DUPLICATE_FORMS)} будут объединены в «${fieldSelections.value.name.value}» и перенесены в архив. ` +
        'Документы дубликатов перейдут к основному контрагенту. Отменить объединение автоматически нельзя.',
      'Запустить объединение?',
      {
        confirmButtonText: 'Объединить',
        cancelButtonText: 'Отмена',
        type: 'warning',
        autofocus: false
      }
    )
  } catch {
    return
  }

  await handleSubmit()
}

async function handleSubmit() {
  if (!canSubmit.value) {
    return
  }

  submitLoading.value = true
  submitError.value = null

  try {
    const response = await api.post('/api/merge-jobs', buildMergeJobRequest())
    await router.push({
      name: 'moysklad-duplicates',
      query: buildDuplicatesReturnQuery({
        mergeQueued: true,
        mergeJobId: response?.mergeJobId
      })
    })
  } catch (requestError) {
    submitError.value = toUserError(requestError, 'Не удалось добавить объединение в очередь')
  } finally {
    submitLoading.value = false
  }
}

function handleCancel() {
  router.push({
    name: 'moysklad-duplicates',
    query: buildDuplicatesReturnQuery()
  })
}

function scrollToStep(id) {
  document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

function normalizeCounterparties(rows) {
  if (!Array.isArray(rows)) {
    return []
  }

  return rows.map((row) => {
    return {
      id: String(row?.id || ''),
      name: String(row?.name || ''),
      description: String(row?.description || ''),
      email: String(row?.email || ''),
      phone: String(row?.phone || ''),
      archived: Boolean(row?.archived),
      updatedAt: String(row?.updatedAt || ''),
      attributes: Array.isArray(row?.attributes)
        ? row.attributes.map(attribute => ({
            id: String(attribute?.id || ''),
            name: String(attribute?.name || ''),
            type: String(attribute?.type || ''),
            value: attribute?.value ?? null,
            file: attribute?.file ?? null,
            valueJson: String(attribute?.valueJson ?? 'null'),
            fileJson: String(attribute?.fileJson ?? 'null')
          })).filter(attribute => attribute.id)
        : []
    }
  }).filter((row) => row.id)
}

function buildDuplicatesReturnQuery(options = {}) {
  const query = {}
  const fields = parseDelimitedQuery(route.query.returnFields)
  const search = normalizeQueryValue(route.query.returnSearch)
  const group = normalizeQueryValue(route.query.returnGroup)

  if (fields.length) {
    query.fields = fields.join(',')
  }

  if (search) {
    query.search = search
  }

  if (group) {
    query.group = group
  }

  if (options.mergeQueued) {
    query.mergeQueued = '1'
  }

  if (options.mergeJobId) {
    query.mergeJobId = String(options.mergeJobId)
  }

  return query
}

function displayValue(value) {
  return value || '—'
}

function createEmptyFieldSelections() {
  return {
    name: {
      sourceCounterpartyId: '',
      value: ''
    },
    description: {
      sourceCounterpartyId: '',
      value: ''
    },
    email: {
      sourceCounterpartyId: '',
      value: ''
    },
    phone: {
      sourceCounterpartyId: '',
      value: ''
    }
  }
}
</script>

<template>
  <div class="app-page merge-page">
    <PageHeader
      title="Объединение контрагентов"
      subtitle="Проверьте, что получится в итоге. Объединение выполняется в фоне — после запуска задача появится в очереди."
      show-back
      back-label="К дубликатам"
      @back="handleCancel"
    >
      <template v-if="headerStatus" #meta>
        <StatusBadge :label="headerStatus.label" :tone="headerStatus.tone" :icon="headerStatus.icon" />
      </template>
    </PageHeader>

    <ErrorNotice
      v-if="error"
      :error="error"
      retryable
      :retrying="loading"
      @retry="loadPreview"
    >
      <template #actions>
        <el-button size="small" @click="handleCancel">К дубликатам</el-button>
      </template>
    </ErrorNotice>

    <!-- Loading skeleton mirrors the final layout -->
    <div v-if="loading && !counterparties.length" class="merge-layout" aria-busy="true">
      <div class="merge-main">
        <SectionPanel v-for="index in 2" :key="index">
          <el-skeleton :rows="4" animated />
        </SectionPanel>
      </div>
      <SectionPanel class="merge-aside">
        <el-skeleton :rows="6" animated />
      </SectionPanel>
    </div>

    <SectionPanel v-else-if="!counterparties.length && !error">
      <EmptyState
        image="select"
        title="Не выбраны контрагенты для объединения"
        description="Для объединения нужно минимум два контрагента. Отметьте их в группе дублей."
      >
        <el-button type="primary" @click="handleCancel">Перейти к дубликатам</el-button>
      </EmptyState>
    </SectionPanel>

    <div v-else-if="counterparties.length" class="merge-layout">
      <div class="merge-main">
        <nav class="merge-steps" aria-label="Этапы объединения">
          <button
            v-for="(step, index) in steps"
            :key="step.id"
            type="button"
            class="merge-steps__item"
            @click="scrollToStep(step.id)"
          >
            <span class="merge-steps__index">{{ index + 1 }}</span>
            <span>{{ step.label }}</span>
            <el-icon v-if="index < steps.length - 1" class="merge-steps__arrow" aria-hidden="true"><Right /></el-icon>
          </button>
        </nav>

        <!-- 1. Primary counterparty -->
        <SectionPanel
          id="merge-step-primary"
          class="merge-section"
          title="1. Основной контрагент"
          subtitle="Эта карточка останется в МоёмСкладе. Остальные станут дубликатами и уйдут в архив."
        >
          <template #actions>
            <span class="app-meta">{{ countLabel(counterparties.length, COUNTERPARTY_FORMS) }}</span>
          </template>

          <div class="merge-candidates" role="radiogroup" aria-label="Основной контрагент">
            <button
              v-for="counterparty in counterparties"
              :key="counterparty.id"
              type="button"
              role="radio"
              class="merge-candidate"
              :class="{
                'merge-candidate--primary': counterparty.id === primaryCounterpartyId,
                'merge-candidate--disabled': counterparty.archived
              }"
              :aria-checked="counterparty.id === primaryCounterpartyId"
              :disabled="counterparty.archived"
              @click="handlePrimaryChange(counterparty.id)"
            >
              <span class="merge-candidate__head">
                <span class="merge-candidate__radio" aria-hidden="true" />
                <span class="merge-candidate__name">{{ displayValue(counterparty.name) }}</span>
              </span>

              <span v-if="counterparty.description" class="merge-candidate__description app-clamp-2">
                {{ counterparty.description }}
              </span>

              <span class="merge-candidate__contacts">
                <span class="merge-candidate__contact">
                  <el-icon aria-hidden="true"><Message /></el-icon>
                  <span class="app-truncate">{{ displayValue(counterparty.email) }}</span>
                </span>
                <span class="merge-candidate__contact">
                  <el-icon aria-hidden="true"><Phone /></el-icon>
                  <span class="app-truncate app-nums">{{ displayValue(counterparty.phone) }}</span>
                </span>
              </span>

              <span class="merge-candidate__footer">
                <StatusBadge
                  v-if="counterparty.id === primaryCounterpartyId"
                  size="sm"
                  tone="primary"
                  :icon="Check"
                  label="Основной"
                />
                <StatusBadge v-else-if="counterparty.archived" size="sm" tone="neutral" label="Архивный — не может быть основным" />
                <StatusBadge v-else size="sm" tone="neutral" :icon="Box" label="Дубликат → в архив" />
                <span class="app-meta app-nums">изм. {{ formatDateTimeShort(counterparty.updatedAt) }}</span>
              </span>
            </button>
          </div>
        </SectionPanel>

        <!-- 2. Final fields -->
        <SectionPanel
          id="merge-step-fields"
          class="merge-section"
          title="2. Итоговые поля"
          subtitle="Для каждого поля выберите значение, которое останется в основной карточке."
          flush
        >
          <template #actions>
            <StatusBadge
              size="sm"
              :tone="changedFields.length ? 'primary' : 'neutral'"
              :label="changedFields.length ? `Изменится полей: ${changedFields.length}` : 'Поля не меняются'"
            />
          </template>

          <div class="merge-fields">
            <div
              v-for="field in fieldRows"
              :key="field.key"
              class="merge-field"
              :class="{ 'merge-field--changed': isFieldChanged(field.key) }"
            >
              <div class="merge-field__label">
                <span class="merge-field__name">
                  {{ field.label }}<span v-if="field.required" class="merge-field__required" aria-label="обязательное">*</span>
                </span>
                <span v-if="isFieldChanged(field.key)" class="merge-field__state merge-field__state--changed">изменится</span>
                <span v-else class="merge-field__state">без изменений</span>
              </div>

              <div class="merge-field__options" role="radiogroup" :aria-label="field.label">
                <button
                  v-for="option in fieldOptions(field.key)"
                  :key="option.value || '__empty'"
                  type="button"
                  role="radio"
                  class="merge-option"
                  :class="{
                    'merge-option--selected': isOptionSelected(field.key, option),
                    'merge-option--empty': !option.value,
                    'merge-option--text': field.type === 'text'
                  }"
                  :aria-checked="isOptionSelected(field.key, option)"
                  :disabled="field.required && !option.value"
                  @click="selectOption(field.key, option)"
                >
                  <span class="merge-option__value">{{ option.value || 'Оставить пустым' }}</span>
                  <span class="merge-option__source">{{ optionSourcesLabel(option) }}</span>
                  <el-icon v-if="isOptionSelected(field.key, option)" class="merge-option__check" aria-hidden="true"><Check /></el-icon>
                </button>
              </div>
            </div>

            <div v-if="attributeRows.length" class="merge-attributes">
              <div
                v-for="attribute in attributeRows"
                :key="attribute.id"
                class="merge-field"
                :class="{ 'merge-field--changed': isAttributeChanged(attribute) }"
              >
                <div class="merge-field__label">
                  <span class="merge-field__name">{{ attribute.name || `Дополнительное поле ${attribute.id}` }}</span>
                  <span class="merge-field__type">{{ attribute.type }}</span>
                  <span v-if="isAttributeChanged(attribute)" class="merge-field__state merge-field__state--changed">изменится</span>
                  <span v-else class="merge-field__state">без изменений</span>
                </div>

                <div class="merge-field__options" role="radiogroup" :aria-label="attribute.name || attribute.id">
                  <button
                    v-for="option in attributeOptions(attribute)"
                    :key="option.key"
                    type="button"
                    role="radio"
                    class="merge-option"
                    :class="{
                      'merge-option--selected': isAttributeOptionSelected(attribute, option),
                      'merge-option--empty': option.clear || isEmptyAttributeValue(option.value),
                      'merge-option--text': attribute.type === 'text'
                    }"
                    :aria-checked="isAttributeOptionSelected(attribute, option)"
                    @click="selectAttributeOption(attribute, option)"
                  >
                    <span class="merge-option__value">{{ option.clear ? 'Очистить поле' : formatAttributeValue(attribute, option.value) }}</span>
                    <span class="merge-option__source">{{ option.clear ? 'значение будет удалено' : optionSourcesLabel(option) }}</span>
                    <el-icon v-if="isAttributeOptionSelected(attribute, option)" class="merge-option__check" aria-hidden="true"><Check /></el-icon>
                  </button>
                </div>
              </div>
            </div>
          </div>
        </SectionPanel>

        <!-- 3. Documents -->
        <SectionPanel
          id="merge-step-documents"
          class="merge-section"
          title="3. Документы дубликатов"
          :subtitle="documents ? 'Документы дубликатов перейдут к основному контрагенту.' : 'Документы дубликатов перейдут к основному контрагенту. Точный список формируется при выполнении — он будет в деталях задачи.'"
        >
          <template v-if="documents" #actions>
            <span class="app-meta app-nums">
              {{ countLabel(documentCounts.total, DOCUMENT_FORMS) }}
            </span>
          </template>

          <MergeDocumentsTable v-if="documents && documents.length" :documents="documents" mode="preview" />
          <EmptyState
            v-else-if="documents"
            size="sm"
            image="documents"
            title="У дубликатов нет документов"
            description="Перепривязывать нечего — после объединения дубликаты просто уйдут в архив."
          />
          <MergeDocumentRules v-else />
        </SectionPanel>

        <!-- 4. Archive -->
        <SectionPanel
          id="merge-step-archive"
          class="merge-section"
          title="4. Архивация дубликатов"
          subtitle="Карточки не удаляются: их можно найти в МоёмСкладе с фильтром «Архивные»."
          flush
        >
          <ul class="merge-archive">
            <li v-for="counterparty in duplicateCounterparties" :key="counterparty.id" class="merge-archive__item">
              <el-icon class="merge-archive__icon" aria-hidden="true"><Box /></el-icon>
              <span class="merge-archive__name app-truncate">{{ displayValue(counterparty.name) }}</span>
              <span class="merge-archive__meta app-truncate">{{ counterparty.email || counterparty.phone }}</span>
              <StatusBadge
                size="sm"
                :tone="counterparty.archived ? 'neutral' : 'warning'"
                :label="counterparty.archived ? 'Уже в архиве' : 'Будет в архиве'"
              />
            </li>
          </ul>
        </SectionPanel>
      </div>

      <!-- 5. Result -->
      <aside id="merge-step-result" class="merge-aside">
        <SectionPanel title="5. Результат">
          <div class="merge-result">
            <div class="merge-result__card">
              <span class="app-meta">Итоговая карточка</span>
              <strong class="merge-result__name">{{ displayValue(fieldSelections.name.value) }}</strong>
              <dl class="merge-result__fields">
                <div>
                  <dt>Email</dt>
                  <dd>{{ displayValue(fieldSelections.email.value) }}</dd>
                </div>
                <div>
                  <dt>Телефон</dt>
                  <dd class="app-nums">{{ displayValue(fieldSelections.phone.value) }}</dd>
                </div>
                <div>
                  <dt>Описание</dt>
                  <dd class="app-clamp-2">{{ displayValue(fieldSelections.description.value) }}</dd>
                </div>
              </dl>
            </div>

            <ul class="merge-result__summary">
              <li>
                <span>Изменится полей</span>
                <strong class="app-nums">{{ changedFields.length }}</strong>
              </li>
              <li>
                <span>Дубликатов в архив</span>
                <strong class="app-nums">{{ duplicateCounterparties.length }}</strong>
              </li>
              <li>
                <span>Документов</span>
                <strong v-if="documents" class="app-nums">
                  {{ documentCounts.total }}
                </strong>
                <span v-else class="app-meta">при выполнении</span>
              </li>
              <li v-if="documents && documentCounts.total" class="merge-result__sub">
                <span>перепривязка / пересоздание</span>
                <span class="app-nums">{{ documentCounts.reassign }} / {{ documentCounts.recreate }}</span>
              </li>
            </ul>

            <div v-if="changedFields.length" class="merge-result__changes">
              <span class="merge-result__block-title">Что изменится</span>
              <div v-for="field in changedFields" :key="field.key" class="merge-result__change">
                <span class="merge-result__change-label">{{ field.label }}</span>
                <span class="merge-result__change-before">{{ field.before || 'пусто' }}</span>
                <span class="merge-result__change-after">{{ field.after || 'пусто' }}</span>
              </div>
            </div>

            <div v-if="problems.length || droppedValues.length" class="merge-result__problems">
              <span class="merge-result__block-title">Обратите внимание</span>
              <p v-for="problem in problems" :key="problem.text" class="merge-result__problem merge-result__problem--danger">
                <el-icon aria-hidden="true"><WarningFilled /></el-icon>
                {{ problem.text }}
              </p>
              <p v-if="droppedValues.length" class="merge-result__problem">
                <el-icon aria-hidden="true"><WarningFilled /></el-icon>
                <span>
                  Не попадут в итоговую карточку:
                  <template v-for="(item, index) in droppedValues.slice(0, 4)" :key="`${item.field}-${item.value}`">
                    <span class="merge-result__dropped">{{ item.field }} «{{ item.value }}»</span><template v-if="index < Math.min(droppedValues.length, 4) - 1">, </template>
                  </template>
                  <template v-if="droppedValues.length > 4"> и ещё {{ droppedValues.length - 4 }}</template>
                </span>
              </p>
            </div>

            <ErrorNotice v-if="submitError" :error="submitError" />
          </div>

          <template #footer>
            <el-button @click="handleCancel">Отмена</el-button>
            <el-button
              type="primary"
              :loading="submitLoading"
              :disabled="!canSubmit"
              @click="confirmAndSubmit"
            >
              Объединить
            </el-button>
          </template>
        </SectionPanel>
      </aside>
    </div>
  </div>
</template>

<style scoped src="../styles/pages/merge.css"></style>
