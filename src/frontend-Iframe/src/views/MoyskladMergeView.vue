<script setup>
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Close } from '@element-plus/icons-vue'
import { api } from '../api/http'

const route = useRoute()
const router = useRouter()

const loading = ref(false)
const previewLoading = ref(false)
const submitLoading = ref(false)
const error = ref(null)
const successMessage = ref(null)
const counterparties = ref([])
const primaryCounterpartyId = ref('')
const fieldSelections = ref(createEmptyFieldSelections())

let previewRequestToken = 0

const primaryCounterparty = computed(() => {
  return counterparties.value.find((counterparty) => counterparty.id === primaryCounterpartyId.value) || null
})

const selectedCounterpartyIds = computed(() => parseCounterpartyIdsQuery(route.query.ids))

const fieldRows = computed(() => [
  { key: 'name', label: 'Название' },
  { key: 'description', label: 'Описание', type: 'text' },
  { key: 'email', label: 'Email' },
  { key: 'phone', label: 'Телефон' }
])

const headerStatusLabel = computed(() => {
  if (submitLoading.value) {
    return 'Добавляем объединение в очередь'
  }

  if (previewLoading.value && !counterparties.value.length) {
    return 'Подготавливаем предпросмотр'
  }

  if (canSubmit.value) {
    return 'Готово к подтверждению'
  }

  return 'Проверяем выбранные данные'
})

const headerStatusClass = computed(() => ({
  'merge-status-pill': true,
  'merge-status-pill--success': canSubmit.value && !submitLoading.value,
  'merge-status-pill--loading': previewLoading.value && !counterparties.value.length
}))

const headerStatusType = computed(() => {
  if (canSubmit.value && !submitLoading.value) return 'success'
  if (previewLoading.value || submitLoading.value) return 'warning'
  return 'info'
})

const canSubmit = computed(() => {
  return !submitLoading.value &&
    counterparties.value.length >= 2 &&
    Boolean(primaryCounterpartyId.value) &&
    !primaryCounterparty.value?.archived &&
    Boolean(fieldSelections.value.name.value.trim())
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
    error.value = 'Выберите хотя бы двух контрагентов для объединения'
    counterparties.value = []
    primaryCounterpartyId.value = ''
    fieldSelections.value = createEmptyFieldSelections()
    return
  }

  loading.value = true
  previewLoading.value = true
  error.value = null
  successMessage.value = null

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
  } catch (requestError) {
    if (requestToken !== previewRequestToken) {
      return
    }
    error.value = requestError.response?.data?.code === 'COUNTERPARTY_BUSY'
      ? 'Один из выбранных контрагентов уже участвует в объединении. Обновите поиск дубликатов.'
      : requestError.message || 'Не удалось загрузить предпросмотр объединения'
    counterparties.value = []
    primaryCounterpartyId.value = ''
    fieldSelections.value = createEmptyFieldSelections()
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
      phone: fieldSelections.value.phone.value || ''
    }
  }
}

async function handleSubmit() {
  if (!canSubmit.value) {
    return
  }

  submitLoading.value = true
  successMessage.value = null
  error.value = null

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
    error.value = requestError.response?.data?.code === 'COUNTERPARTY_BUSY'
      ? 'Один из выбранных контрагентов уже участвует в объединении. Обновите поиск дубликатов.'
      : requestError.message || 'Не удалось добавить объединение в очередь'
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
      updatedAt: String(row?.updatedAt || '')
    }
  }).filter((row) => row.id)
}

function isTextField(field) {
  const normalizedType = String(field?.type || '').trim().toLowerCase()
  return normalizedType === 'text' || normalizedType === 'longtext'
}

function isLargeTextValue(value) {
  const normalizedValue = String(value || '')
  return normalizedValue.length > 100 || normalizedValue.includes('\n')
}

function valuePreviewClass(value, forceExpanded = false) {
  return {
    'merge-value-preview': true,
    'merge-value-preview--expanded': forceExpanded || isLargeTextValue(value)
  }
}

function parseCounterpartyIdsQuery(rawValue) {
  const values = Array.isArray(rawValue) ? rawValue : [rawValue]
  const result = []
  const seen = new Set()

  for (const value of values) {
    const parts = String(value || '')
      .split(',')
      .map((item) => item.trim())
      .filter(Boolean)

    for (const part of parts) {
      if (seen.has(part)) {
        continue
      }

      seen.add(part)
      result.push(part)
    }
  }

  return result
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

function parseDelimitedQuery(rawValue) {
  const values = Array.isArray(rawValue) ? rawValue : [rawValue]
  const result = []

  for (const value of values) {
    const parts = String(value || '')
      .split(',')
      .map((item) => item.trim())
      .filter(Boolean)

    for (const part of parts) {
      if (!result.includes(part)) {
        result.push(part)
      }
    }
  }

  return result
}

function normalizeQueryValue(rawValue) {
  if (Array.isArray(rawValue)) {
    return String(rawValue[0] || '').trim()
  }

  return String(rawValue || '').trim()
}

function displayValue(value) {
  return value || '—'
}

function sourceCounterpartyName(counterpartyId) {
  if (!counterpartyId) {
    return '—'
  }

  const counterparty = counterparties.value.find((item) => item.id === counterpartyId)
  return counterparty?.name || '—'
}

function formatDateTime(value) {
  if (!value) {
    return '—'
  }

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit'
  }).format(date)
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
  <main class="merge-page">
    <el-dialog
      :model-value="true"
      class="merge-dialog"
      fullscreen
      :show-close="false"
      :close-on-click-modal="false"
      :close-on-press-escape="false"
      @close="handleCancel"
    >
      <template #header>
        <header class="merge-surface__topbar">
          <div class="merge-surface__title-block">
            <p class="merge-surface__eyebrow">Дубликаты контрагентов</p>
            <h1 class="merge-surface__title">Предпросмотр объединения</h1>
            <p class="merge-surface__subtitle">
              Проверьте выбранные значения полей перед добавлением объединения в очередь.
            </p>
          </div>

          <div class="merge-surface__toolbar">
            <el-tag :type="headerStatusType" effect="light">{{ headerStatusLabel }}</el-tag>
            <el-button circle text aria-label="Закрыть merge" @click="handleCancel">
              <el-icon><Close /></el-icon>
            </el-button>
          </div>
        </header>
      </template>

      <el-skeleton v-if="loading && !counterparties.length" :rows="6" animated />
      <el-alert v-else-if="error" :title="error" type="error" :closable="false" show-icon />
      <el-alert v-else-if="successMessage" :title="successMessage" type="success" :closable="false" show-icon />

      <div v-if="counterparties.length" class="merge-workspace">
        <section class="merge-panel">
          <div class="merge-panel__header merge-panel__header--spread">
            <div>
              <h2>Выберите контрагентов для объединения</h2>
              <p>Главный контрагент становится целевой карточкой; итоговые поля выбираются отдельно ниже.</p>
            </div>
            <el-tag type="info" effect="plain">{{ counterparties.length }} выбрано в группе</el-tag>
          </div>

          <div class="merge-counterparty-grid">
            <el-card
              v-for="counterparty in counterparties"
              :key="counterparty.id"
              class="merge-counterparty-card"
              :class="{ 'merge-counterparty-card--primary': counterparty.id === primaryCounterpartyId }"
              shadow="never"
            >
              <div class="merge-counterparty-card__top">
                <el-radio
                  :model-value="primaryCounterpartyId"
                  :value="counterparty.id"
                  :disabled="counterparty.archived"
                  @change="handlePrimaryChange(counterparty.id)"
                >
                  <span class="merge-counterparty-card__identity">
                    <span class="merge-counterparty-card__title-row">
                      <strong>{{ displayValue(counterparty.name) }}</strong>
                      <el-tag
                        v-if="counterparty.id === primaryCounterpartyId"
                        type="primary"
                        size="small"
                        effect="light"
                      >
                        Главный
                      </el-tag>
                    </span>
                    <span>{{ displayValue(counterparty.description) }}</span>
                  </span>
                </el-radio>

                <el-tag :type="counterparty.archived ? 'info' : 'success'" size="small" effect="light">
                  {{ counterparty.archived ? 'Архивный' : 'Активный' }}
                </el-tag>
              </div>

              <dl class="merge-counterparty-card__details">
                <div>
                  <dt>Email</dt>
                  <dd>{{ displayValue(counterparty.email) }}</dd>
                </div>
                <div>
                  <dt>Телефон</dt>
                  <dd>{{ displayValue(counterparty.phone) }}</dd>
                </div>
              </dl>
              <div class="merge-counterparty-card__timeline">
                Обновлен: {{ formatDateTime(counterparty.updatedAt) }}
              </div>
            </el-card>
          </div>
        </section>

        <section class="merge-panel">
          <div class="merge-panel__header">
            <h2>Поля итоговой карточки</h2>
            <p>Выберите источник значения для каждого основного поля merge.</p>
          </div>

          <el-table :data="fieldRows" class="merge-table" border table-layout="auto">
            <el-table-column prop="label" label="Поле" fixed="left" min-width="120" />
            <el-table-column
              v-for="counterparty in counterparties"
              :key="counterparty.id"
              :label="displayValue(counterparty.name)"
              min-width="180"
            >
              <template #default="{ row: field }">
                <el-radio
                  :model-value="fieldSelections[field.key].sourceCounterpartyId"
                  :value="counterparty.id"
                  @change="handleFieldSelection(field.key, counterparty.id)"
                >
                  <span :class="valuePreviewClass(displayValue(counterparty[field.key]), isTextField(field))">
                    {{ displayValue(counterparty[field.key]) }}
                  </span>
                </el-radio>
              </template>
            </el-table-column>
            <el-table-column label="Итоговое значение" fixed="right" min-width="200">
              <template #default="{ row: field }">
                <div class="merge-table__result">
                  <div :class="valuePreviewClass(fieldSelections[field.key].value || '—', isTextField(field))">
                    {{ fieldSelections[field.key].value || '—' }}
                  </div>
                  <span class="merge-table__result-source">
                    Источник: {{ sourceCounterpartyName(fieldSelections[field.key].sourceCounterpartyId) }}
                  </span>
                </div>
              </template>
            </el-table-column>
          </el-table>
        </section>
      </div>

      <el-empty
        v-else-if="!loading && !error"
        description="Для merge нужно выбрать минимум двух контрагентов"
        :image-size="80"
      />

      <template #footer>
        <div v-if="counterparties.length" class="merge-actions">
          <el-button @click="handleCancel">Отмена</el-button>
          <el-button
            type="primary"
            :loading="submitLoading"
            :disabled="!canSubmit"
            @click="handleSubmit"
          >
            Добавить в очередь
          </el-button>
        </div>
      </template>
    </el-dialog>
  </main>
</template>

<style scoped src="../styles/pages/merge.css"></style>
