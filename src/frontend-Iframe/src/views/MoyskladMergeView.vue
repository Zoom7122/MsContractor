<script setup>
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
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
    error.value = requestError.message || 'Не удалось загрузить предпросмотр объединения'
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
    error.value = requestError.message || 'Не удалось добавить объединение в очередь'
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
    <section class="merge-surface">
      <header class="merge-surface__topbar">
        <div class="merge-surface__title-block">
          <p class="merge-surface__eyebrow">Дубликаты контрагентов</p>
          <h1 class="merge-surface__title">Предпросмотр объединения</h1>
          <p class="merge-surface__subtitle">
            Проверьте выбранные значения полей, документы и доп. поля перед добавлением объединения в очередь.
          </p>
        </div>

        <div class="merge-surface__toolbar">
          <span :class="headerStatusClass">
            {{ headerStatusLabel }}
          </span>
          <button class="merge-icon-button" type="button" aria-label="Закрыть merge" @click="handleCancel">
            ×
          </button>
        </div>
      </header>

      <div class="merge-notices">
        <p v-if="loading && !counterparties.length" class="merge-notice">
          {{ previewLoading ? 'Загрузка предпросмотра объединения...' : 'Подготавливаем данные...' }}
        </p>
        <p v-else-if="error" class="merge-notice merge-notice--error">{{ error }}</p>
        <p v-else-if="successMessage" class="merge-notice merge-notice--success">{{ successMessage }}</p>
      </div>

      <div v-if="counterparties.length" class="merge-workspace">
        <div class="merge-workspace__main">
          <section class="merge-panel">
            <div class="merge-panel__header merge-panel__header--spread">
              <div>
                <h2>Выберите контрагентов для объединения</h2>
                <p>Главный контрагент становится целевой карточкой merge; итоговые поля выбираются отдельно ниже.</p>
              </div>
              <div class="merge-panel__meta">
                <strong>{{ counterparties.length }}</strong>
                <span>выбрано в группе</span>
              </div>
            </div>

            <div class="merge-counterparty-scroll">
              <div class="merge-counterparty-grid">
                <label
                  v-for="counterparty in counterparties"
                  :key="counterparty.id"
                  class="merge-counterparty-card"
                  :class="{ 'merge-counterparty-card--primary': counterparty.id === primaryCounterpartyId }"
                >
                  <div class="merge-counterparty-card__top">
                    <div class="merge-counterparty-card__radio-wrap">
                      <input
                        :checked="counterparty.id === primaryCounterpartyId"
                        type="radio"
                        name="primaryCounterparty"
                        :disabled="counterparty.archived"
                        @change="handlePrimaryChange(counterparty.id)"
                      />
                    </div>

                    <div class="merge-counterparty-card__identity">
                      <div class="merge-counterparty-card__title-row">
                        <strong>{{ displayValue(counterparty.name) }}</strong>
                        <span
                          v-if="counterparty.id === primaryCounterpartyId"
                          class="merge-counterparty-card__primary-chip"
                        >
                          Главный
                        </span>
                      </div>
                      <p>{{ displayValue(counterparty.description) }}</p>
                    </div>

                    <span class="merge-counterparty-card__badge">
                      {{ counterparty.archived ? 'Архивный' : 'Активный' }}
                    </span>
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
                    <span>Обновлен: {{ formatDateTime(counterparty.updatedAt) }}</span>
                  </div>
                </label>
              </div>
            </div>
          </section>

          <section class="merge-panel">
            <div class="merge-panel__header">
              <h2>Поля итоговой карточки</h2>
              <p>Здесь остаётся весь текущий функционал ручного выбора значений для основных полей merge.</p>
            </div>

            <div class="merge-table-wrap merge-table-wrap--main-fields">
              <table class="merge-table">
                <thead>
                  <tr>
                    <th>Поле</th>
                    <th v-for="counterparty in counterparties" :key="counterparty.id">
                      {{ displayValue(counterparty.name) }}
                    </th>
                    <th>Итоговое значение</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="field in fieldRows" :key="field.key">
                    <td class="merge-table__field">{{ field.label }}</td>
                    <td v-for="counterparty in counterparties" :key="`${field.key}-${counterparty.id}`">
                      <label class="merge-radio-option">
                        <input
                          :checked="fieldSelections[field.key].sourceCounterpartyId === counterparty.id"
                          type="radio"
                          :name="`field-${field.key}`"
                          @change="handleFieldSelection(field.key, counterparty.id)"
                        />
                        <span :class="valuePreviewClass(displayValue(counterparty[field.key]), isTextField(field))">
                          {{ displayValue(counterparty[field.key]) }}
                        </span>
                      </label>
                    </td>
                    <td class="merge-table__result">
                      <div :class="valuePreviewClass(fieldSelections[field.key].value || '—', isTextField(field))">
                        {{ fieldSelections[field.key].value || '—' }}
                      </div>
                      <span class="merge-table__result-source">
                        Источник: {{ sourceCounterpartyName(fieldSelections[field.key].sourceCounterpartyId) }}
                      </span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </section>

        </div>

      </div>

      <div v-else-if="!loading && !error" class="merge-empty-state merge-empty-state--surface">
        Для merge нужно выбрать минимум двух контрагентов.
      </div>

      <footer v-if="counterparties.length" class="merge-surface__footer">
        <div class="merge-actions">
          <button class="merge-button merge-button--ghost" type="button" @click="handleCancel">
            Отмена
          </button>
          <button class="merge-button merge-button--primary" type="button" :disabled="!canSubmit" @click="handleSubmit">
            {{ submitLoading ? 'Добавляем...' : 'Добавить в очередь' }}
          </button>
        </div>
      </footer>
    </section>
  </main>
</template>

<style scoped>
.merge-page {
  min-width: 0;
  max-width: 100%;
  min-height: 100%;
  padding: 28px 28px 36px;
  overflow-x: hidden;
  background:
    radial-gradient(circle at top left, rgba(31, 93, 206, 0.15), transparent 26%),
    radial-gradient(circle at right center, rgba(11, 33, 78, 0.12), transparent 24%),
    linear-gradient(180deg, #eef4fb 0%, #f6f8fc 100%);
}

.merge-surface {
  display: grid;
  gap: 18px;
  min-width: 0;
  max-width: 100%;
  padding: 22px;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.92);
  border: 1px solid rgba(208, 220, 239, 0.85);
  border-radius: 28px;
  box-shadow: 0 24px 60px rgba(17, 34, 68, 0.12);
  backdrop-filter: blur(10px);
}

.merge-surface__topbar {
  display: flex;
  min-width: 0;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
}

.merge-surface__title-block {
  min-width: 0;
}

.merge-surface__eyebrow {
  margin: 0 0 8px;
  color: #2d61c4;
  font-size: 12px;
  font-weight: 800;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.merge-surface__title {
  margin: 0;
  color: #12203f;
  font-size: 34px;
  font-weight: 900;
  line-height: 1.1;
}

.merge-surface__subtitle {
  max-width: 760px;
  margin: 10px 0 0;
  color: #667699;
  font-size: 15px;
  line-height: 1.6;
}

.merge-surface__toolbar {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 10px;
}

.merge-status-pill {
  display: inline-flex;
  align-items: center;
  min-height: 42px;
  padding: 0 16px;
  color: #274b8d;
  font-size: 13px;
  font-weight: 800;
  background: #eef4ff;
  border: 1px solid #cddbf7;
  border-radius: 999px;
  white-space: nowrap;
}

.merge-status-pill--warning {
  color: #9b5d00;
  background: #fff5df;
  border-color: #f5d9a1;
}

.merge-status-pill--success {
  color: #1f7a43;
  background: #e9f8ef;
  border-color: #b7e2c8;
}

.merge-status-pill--loading {
  color: #2d61c4;
  background: #edf4ff;
  border-color: #bfd0f3;
}

.merge-icon-button {
  width: 42px;
  height: 42px;
  border: 1px solid #d9e3f3;
  border-radius: 14px;
  color: #546689;
  font-size: 24px;
  line-height: 1;
  background: #ffffff;
  cursor: pointer;
}

.merge-notices {
  display: grid;
  min-width: 0;
  gap: 10px;
}

.merge-notice {
  margin: 0;
  padding: 12px 14px;
  color: #324971;
  font-size: 14px;
  line-height: 1.5;
  background: #f5f8fd;
  border: 1px solid #dbe5f4;
  border-radius: 14px;
}

.merge-notice--error {
  color: #b42318;
  background: #fef3f2;
  border-color: #f6c9c5;
}

.merge-notice--success {
  color: #157347;
  background: #eefaf2;
  border-color: #c7e8d2;
}

.merge-notice--info {
  color: #8b5b00;
  background: #fff8eb;
  border-color: #f3deb1;
}

.merge-workspace {
  display: grid;
  min-width: 0;
  max-width: 100%;
  gap: 18px;
  align-items: start;
}

.merge-workspace__main {
  display: grid;
  min-width: 0;
  max-width: 100%;
  gap: 18px;
}

.merge-panel {
  min-width: 0;
  max-width: 100%;
  overflow: hidden;
  background: #ffffff;
  border: 1px solid #dce6f3;
  border-radius: 22px;
  box-shadow: 0 14px 34px rgba(15, 35, 80, 0.06);
}

.merge-panel {
  padding: 22px;
}

.merge-panel__header {
  min-width: 0;
  margin-bottom: 16px;
}

.merge-panel__header--spread {
  display: flex;
  min-width: 0;
  align-items: flex-start;
  justify-content: space-between;
  gap: 18px;
}

.merge-panel__header h2 {
  margin: 0;
  color: #12203f;
  font-size: 24px;
  font-weight: 850;
  line-height: 1.2;
}

.merge-panel__header p {
  margin: 8px 0 0;
  color: #697a9d;
  font-size: 14px;
  line-height: 1.55;
}

.merge-panel__meta {
  display: grid;
  justify-items: end;
  gap: 4px;
  min-width: 100px;
}

.merge-panel__meta strong {
  color: #12203f;
  font-size: 28px;
  font-weight: 900;
}

.merge-panel__meta span {
  color: #7b8ba8;
  font-size: 12px;
  font-weight: 700;
}

.merge-counterparty-scroll {
  max-width: 100%;
  overflow-x: auto;
  overflow-y: hidden;
  overscroll-behavior-x: contain;
  scrollbar-gutter: stable;
  padding: 12px;
  background: #fbfdff;
  border: 1px solid #e2eaf5;
  border-radius: 18px;
}

.merge-counterparty-grid {
  display: grid;
  grid-auto-columns: minmax(280px, 340px);
  grid-auto-flow: column;
  grid-template-columns: none;
  gap: 16px;
  width: max-content;
  min-width: 100%;
}

.merge-counterparty-card {
  display: grid;
  width: 100%;
  min-width: 0;
  gap: 14px;
  padding: 18px;
  background: linear-gradient(180deg, #fbfdff 0%, #f6f9fe 100%);
  border: 1px solid #dfe7f3;
  border-radius: 18px;
  cursor: pointer;
  transition: transform 0.18s ease, box-shadow 0.18s ease, border-color 0.18s ease;
}

.merge-counterparty-card:hover {
  transform: translateY(-1px);
  box-shadow: 0 14px 24px rgba(30, 54, 100, 0.08);
}

.merge-counterparty-card--primary {
  background: linear-gradient(180deg, #eef5ff 0%, #fbfdff 100%);
  border-color: #7aa4ed;
  box-shadow: 0 18px 32px rgba(45, 108, 223, 0.12);
}

.merge-counterparty-card__top {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  gap: 12px;
  align-items: start;
}

.merge-counterparty-card__radio-wrap {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  padding-top: 2px;
}

.merge-counterparty-card__identity {
  min-width: 0;
}

.merge-counterparty-card__title-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.merge-counterparty-card__identity strong {
  color: #12203f;
  font-size: 18px;
  font-weight: 850;
  line-height: 1.35;
}

.merge-counterparty-card__identity p {
  max-height: 112px;
  margin: 8px 0 0;
  overflow-x: hidden;
  overflow-y: auto;
  overflow-wrap: anywhere;
  color: #5e7194;
  font-size: 14px;
  line-height: 1.5;
  white-space: pre-wrap;
}

.merge-counterparty-card__primary-chip {
  display: inline-flex;
  align-items: center;
  min-height: 24px;
  padding: 0 10px;
  color: #1f5ed4;
  font-size: 12px;
  font-weight: 800;
  background: #e8f0ff;
  border-radius: 999px;
}

.merge-counterparty-card__badge {
  display: inline-flex;
  align-items: center;
  min-height: 28px;
  padding: 0 10px;
  color: #4e6187;
  font-size: 12px;
  font-weight: 800;
  background: #edf2fb;
  border-radius: 999px;
  white-space: nowrap;
}

.merge-counterparty-card__badge--loading {
  color: #9b5d00;
  background: #fff1d6;
}

.merge-counterparty-card__badge--ready {
  color: #157347;
  background: #e5f7ee;
}

.merge-counterparty-card__badge--partial {
  color: #7b4bc4;
  background: #f0e7ff;
}

.merge-counterparty-card__badge--error {
  color: #b42318;
  background: #fee4e2;
}

.merge-counterparty-card__details {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  margin: 0;
}

.merge-counterparty-card__details div {
  display: grid;
  gap: 4px;
}

.merge-counterparty-card__details dt {
  color: #7a8aa8;
  font-size: 12px;
  font-weight: 700;
}

.merge-counterparty-card__details dd {
  margin: 0;
  color: #12203f;
  font-size: 13px;
  font-weight: 600;
  line-height: 1.45;
  word-break: break-word;
}

.merge-counterparty-card__timeline,
.merge-counterparty-card__footer {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  color: #697a9d;
  font-size: 12px;
  line-height: 1.45;
}

.merge-counterparty-card__timeline span,
.merge-counterparty-card__footer span {
  min-width: 0;
}

.merge-table-wrap {
  width: 100%;
  max-width: 100%;
  overflow: auto;
  overscroll-behavior: contain;
  scrollbar-gutter: stable;
  border: 1px solid #e2eaf5;
  border-radius: 18px;
}

.merge-table-wrap--main-fields {
  max-height: clamp(280px, 42vh, 430px);
}

.merge-table-wrap--attribute-fields {
  max-height: clamp(320px, 48vh, 520px);
}

.merge-table {
  width: max-content;
  min-width: 100%;
  border-collapse: separate;
  border-spacing: 0;
}

.merge-table th,
.merge-table td {
  padding: 14px 12px;
  vertical-align: top;
  text-align: left;
  border-bottom: 1px solid #e5ecf6;
  width: 240px;
  min-width: 220px;
  max-width: 260px;
}

.merge-table th {
  position: sticky;
  top: 0;
  z-index: 4;
  color: #50648f;
  font-size: 13px;
  font-weight: 800;
  background: #f7faff;
}

.merge-table td {
  color: #12203f;
  font-size: 13px;
  background: #ffffff;
}

.merge-table tbody tr:last-child td {
  border-bottom: none;
}

.merge-table th:first-child,
.merge-table td:first-child {
  position: sticky;
  left: 0;
  z-index: 3;
  width: 190px;
  min-width: 180px;
  max-width: 220px;
}

.merge-table th:first-child {
  z-index: 6;
}

.merge-table th:last-child,
.merge-table td:last-child {
  position: sticky;
  right: 0;
  z-index: 3;
  width: 240px;
  min-width: 210px;
  max-width: 260px;
}

.merge-table th:last-child {
  z-index: 6;
}

.merge-table__field {
  min-width: 180px;
  font-weight: 800;
  background: #ffffff;
}

.merge-table__field-note {
  display: block;
  margin-top: 4px;
  color: #7182a4;
  font-size: 12px;
  font-weight: 600;
}

.merge-table__result {
  min-width: 210px;
  color: #173772;
  font-weight: 700;
  background: #fbfdff;
}

.merge-table__result-source {
  display: block;
  margin-top: 8px;
  color: #7182a4;
  font-size: 12px;
  font-weight: 700;
  line-height: 1.35;
}

.merge-radio-option {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  cursor: pointer;
  min-width: 0;
}

.merge-radio-option input {
  flex: 0 0 auto;
  margin-top: 3px;
}

.merge-radio-option span {
  line-height: 1.5;
  word-break: break-word;
}

.merge-value-preview {
  display: block;
  max-width: 100%;
  box-sizing: border-box;
  min-width: 0;
  overflow-wrap: anywhere;
  word-break: break-word;
  white-space: pre-wrap;
}

.merge-value-preview--expanded {
  max-height: 112px;
  padding: 8px 10px;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
  background: #f8fbff;
  border: 1px solid #d8e4f5;
  border-radius: 10px;
}

.merge-document-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;
}

.merge-document-card {
  display: grid;
  gap: 8px;
  padding: 18px;
  background: linear-gradient(180deg, #fbfdff 0%, #f5f9ff 100%);
  border: 1px solid #dce6f4;
  border-radius: 18px;
}

.merge-document-card__label {
  color: #5f7193;
  font-size: 12px;
  font-weight: 800;
}

.merge-document-card strong {
  color: #12203f;
  font-size: 30px;
  font-weight: 900;
  line-height: 1;
}

.merge-document-card p {
  margin: 0;
  color: #7182a4;
  font-size: 12px;
  line-height: 1.5;
}

.merge-empty-state {
  padding: 16px 18px;
  color: #637599;
  font-size: 14px;
  line-height: 1.6;
  background: #f8fbff;
  border: 1px dashed #d7e3f6;
  border-radius: 18px;
}

.merge-empty-state--surface {
  margin-top: 4px;
}

.merge-surface__footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 16px 18px 0;
  border-top: 1px solid #e7edf6;
}

.merge-surface__footer-note {
  display: grid;
  gap: 6px;
  max-width: 680px;
  color: #6d7ea0;
  font-size: 13px;
  line-height: 1.55;
}

.merge-surface__footer-note strong {
  color: #12203f;
  font-size: 13px;
  font-weight: 850;
}

.merge-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.merge-button {
  min-height: 48px;
  padding: 0 20px;
  border: 1px solid transparent;
  border-radius: 14px;
  font-size: 14px;
  font-weight: 800;
  cursor: pointer;
  transition: transform 0.18s ease, box-shadow 0.18s ease, opacity 0.18s ease;
}

.merge-button:hover:not(:disabled) {
  transform: translateY(-1px);
}

.merge-button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.merge-button--primary {
  color: #ffffff;
  background: linear-gradient(180deg, #2d6cdf 0%, #1f58c4 100%);
  border-color: #2d6cdf;
  box-shadow: 0 12px 24px rgba(45, 108, 223, 0.2);
}

.merge-button--ghost {
  color: #2d6cdf;
  background: #ffffff;
  border-color: #b5c9ef;
}

@media (max-width: 1180px) {
  .merge-document-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 900px) {
  .merge-page {
    padding: 18px;
  }

  .merge-surface {
    padding: 18px;
    border-radius: 22px;
  }

  .merge-surface__topbar,
  .merge-panel__header--spread,
  .merge-surface__footer {
    flex-direction: column;
    align-items: stretch;
  }

  .merge-surface__toolbar {
    justify-content: space-between;
  }

  .merge-document-grid,
  .merge-counterparty-card__details {
    grid-template-columns: 1fr;
  }

  .merge-counterparty-card__top {
    grid-template-columns: auto minmax(0, 1fr);
  }

  .merge-counterparty-card__badge {
    grid-column: 2;
    justify-self: start;
  }

  .merge-counterparty-card__timeline,
  .merge-counterparty-card__footer,
  .merge-actions {
    flex-direction: column;
    align-items: stretch;
  }

  .merge-button,
  .merge-icon-button {
    width: 100%;
  }
}
</style>
.merge-panel__header--spread > div:first-child {
  min-width: 0;
}
