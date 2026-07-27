<script setup>
import { computed, onMounted, ref, watch } from 'vue'

import {
  getCounterpartyAttributes,
  saveCounterpartyAttributeMergeSettings
} from '../api/counterparties'
import {
  getDuplicateExclusions,
  getDuplicateSearchOptions,
  getFullSyncTimer,
  saveDuplicateExclusions,
  saveDuplicateSearchOptions,
  saveFullSyncTimer
} from '../api/settings'

const props = defineProps({
  settingsData: {
    type: Object,
    default: null
  }
})

const defaultSettings = {
  duplicateExclusions: [],
  duplicateSearchOptions: {
    includeArchivedWithDocuments: true
  },
  fullSyncTimer: {
    enabled: false,
    runAt: '03:00'
  },
  mergeAttributes: [],
  searchLimits: {
    groupLimit: 200,
    itemLimit: 200
  }
}

const exclusionFieldOptions = [
  { value: 'name', label: 'Имя' },
  { value: 'email', label: 'Email' },
  { value: 'phone', label: 'Телефон' }
]

const initialSettings = ref(cloneSettings(defaultSettings))
const form = ref(cloneSettings(defaultSettings))
const loading = ref(false)
const loadError = ref(null)
const saving = ref(false)
const message = ref(null)
const saveError = ref(null)
const exclusionsOpen = ref(false)
const mergeAttributesOpen = ref(false)
const newExclusion = ref({
  field: 'email',
  value: ''
})

const hasExclusions = computed(() => form.value.duplicateExclusions.length > 0)
const hasMergeAttributes = computed(() => form.value.mergeAttributes.length > 0)
const enabledMergeAttributesCount = computed(() =>
  form.value.mergeAttributes.filter((item) => item.enabled).length
)

watch(
  () => props.settingsData,
  (value) => {
    if (!value) {
      return
    }

    const normalized = normalizeSettings(value)
    initialSettings.value = cloneSettings(normalized)
    form.value = cloneSettings(normalized)
  },
  { immediate: true }
)

onMounted(() => {
  if (props.settingsData) {
    return
  }

  loadSettings()
})

async function loadSettings() {
  loading.value = true
  loadError.value = null

  try {
    const [duplicateExclusions, duplicateSearchOptions, fullSyncTimer, mergeAttributes] = await Promise.all([
      getDuplicateExclusions(),
      getDuplicateSearchOptions(),
      getFullSyncTimer(),
      getCounterpartyAttributes()
    ])

    const normalized = normalizeSettings({
      duplicateExclusions,
      duplicateSearchOptions,
      fullSyncTimer,
      mergeAttributes
    })
    initialSettings.value = cloneSettings(normalized)
    form.value = cloneSettings(normalized)
  } catch (error) {
    loadError.value = error.message || 'Не удалось загрузить настройки'
  } finally {
    loading.value = false
  }
}

function normalizeSettings(source) {
  const duplicateExclusions = Array.isArray(source?.duplicateExclusions)
    ? source.duplicateExclusions
        .map((item) => ({
          field: normalizeExclusionField(item?.field),
          value: String(item?.value || '').trim()
        }))
        .filter((item) => item.field && item.value)
    : []

  return {
    duplicateExclusions,
    duplicateSearchOptions: {
      includeArchivedWithDocuments:
        source?.duplicateSearchOptions?.includeArchivedWithDocuments ??
        defaultSettings.duplicateSearchOptions.includeArchivedWithDocuments
    },
    fullSyncTimer: {
      enabled: Boolean(source?.fullSyncTimer?.enabled ?? defaultSettings.fullSyncTimer.enabled),
      runAt: normalizeTimeValue(source?.fullSyncTimer?.runAt, defaultSettings.fullSyncTimer.runAt)
    },
    mergeAttributes: normalizeMergeAttributes(source?.mergeAttributes),
    searchLimits: {
      groupLimit: clampLimit(
        source?.searchLimits?.groupLimit ?? source?.duplicateSearchOptions?.groupLimit,
        defaultSettings.searchLimits.groupLimit
      ),
      itemLimit: clampLimit(
        source?.searchLimits?.itemLimit ?? source?.duplicateSearchOptions?.itemLimit,
        defaultSettings.searchLimits.itemLimit
      )
    }
  }
}

function normalizeMergeAttributes(items) {
  if (!Array.isArray(items)) {
    return []
  }

  return items
    .map((item) => ({
      attributeId: String(item?.id || item?.attributeId || '').trim(),
      name: String(item?.name || '').trim(),
      type: String(item?.type || '').trim(),
      required: Boolean(item?.required),
      enabled: Boolean(item?.enabled)
    }))
    .filter((item) => item.attributeId && item.name)
    .sort((left, right) => left.name.localeCompare(right.name, 'ru'))
}

function cloneSettings(value) {
  return JSON.parse(JSON.stringify(value))
}

function normalizeExclusionField(value) {
  const normalized = String(value || '').trim()
  return exclusionFieldOptions.some((item) => item.value === normalized) ? normalized : ''
}

function normalizeTimeValue(value, fallback) {
  const normalized = String(value || '').trim()
  return /^\d{2}:\d{2}$/.test(normalized) ? normalized : fallback
}

function clampLimit(value, fallback) {
  const numeric = Number(value)
  if (!Number.isFinite(numeric)) {
    return fallback
  }

  return Math.min(1000, Math.max(1, Math.trunc(numeric)))
}

function exclusionFieldLabel(field) {
  return exclusionFieldOptions.find((item) => item.value === field)?.label || field
}

function addExclusion() {
  const field = normalizeExclusionField(newExclusion.value.field)
  const value = String(newExclusion.value.value || '').trim()
  if (!field || !value) {
    return
  }

  form.value.duplicateExclusions.push({ field, value })
  newExclusion.value = {
    field: 'email',
    value: ''
  }
}

function removeExclusion(index) {
  form.value.duplicateExclusions.splice(index, 1)
}

function toggleMergeAttribute(attributeId) {
  form.value.mergeAttributes = form.value.mergeAttributes.map((item) => {
    if (item.attributeId !== attributeId) {
      return item
    }

    return {
      ...item,
      enabled: !item.enabled
    }
  })
}

function resetSettings() {
  form.value = cloneSettings(defaultSettings)
  message.value = null
  saveError.value = null
}

function cancelChanges() {
  form.value = cloneSettings(initialSettings.value)
  message.value = null
  saveError.value = null
}

async function saveSettings() {
  if (saving.value) {
    return
  }

  saving.value = true
  message.value = null
  saveError.value = null

  try {
    if (!props.settingsData) {
      await Promise.all([
        saveDuplicateExclusions(form.value.duplicateExclusions),
        saveDuplicateSearchOptions({
          ...form.value.duplicateSearchOptions,
          groupLimit: form.value.searchLimits.groupLimit,
          itemLimit: form.value.searchLimits.itemLimit
        }),
        saveFullSyncTimer(form.value.fullSyncTimer),
        saveCounterpartyAttributeMergeSettings(
          form.value.mergeAttributes.map((item) => ({
            attributeId: item.attributeId,
            enabled: item.enabled
          }))
        )
      ])
    }

    initialSettings.value = cloneSettings(form.value)
    message.value = 'Настройки сохранены'
  } catch (error) {
    saveError.value = error.message || 'Не удалось сохранить настройки'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <main class="settings-page">
    <header class="settings-page__header">
      <h1 class="settings-page__title">Настройки</h1>
      <p class="settings-page__subtitle">Управление подключением, merge-логикой и параметрами работы MS Contractor</p>
    </header>

    <p v-if="loading" class="settings-message">Загрузка настроек...</p>
    <p v-if="loadError" class="settings-error">Не удалось загрузить настройки</p>

    <section class="settings-card">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Исключения</h2>
        <p class="settings-card__description">
          Вы можете исключить email, телефон или имя, которые не будут попадать в дубликаты.
        </p>
      </div>

      <div class="settings-card__controls">
        <div class="settings-attributes-summary">
          <span>Всего исключений</span>
          <strong>{{ form.duplicateExclusions.length }}</strong>
        </div>

        <button class="settings-button settings-button--outline" type="button" @click="exclusionsOpen = true">
          Открыть исключения
        </button>
      </div>
    </section>

    <section class="settings-card">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Доп. поля в объединении</h2>
        <p class="settings-card__description">
          Выберите дополнительные поля контрагентов, которые должны отображаться в merge-компоненте и участвовать в выборе итогового значения.
        </p>
      </div>

      <div class="settings-card__controls">
        <div class="settings-attributes-summary">
          <span>Выбрано для merge</span>
          <strong>{{ enabledMergeAttributesCount }}</strong>
        </div>

        <button class="settings-button settings-button--outline" type="button" @click="mergeAttributesOpen = !mergeAttributesOpen">
          {{ mergeAttributesOpen ? 'Скрыть доп. поля' : 'Открыть доп. поля' }}
        </button>

        <div v-if="mergeAttributesOpen" class="merge-attributes-list">
          <div v-if="!hasMergeAttributes" class="merge-attribute merge-attribute--empty">
            Дополнительные поля не найдены
          </div>

          <label
            v-for="item in form.mergeAttributes"
            :key="item.attributeId"
            class="merge-attribute"
          >
            <div class="merge-attribute__main">
              <input
                :checked="item.enabled"
                type="checkbox"
                @change="toggleMergeAttribute(item.attributeId)"
              />
              <div>
                <strong>{{ item.name }}</strong>
                <p>{{ item.type || 'unknown' }}<span v-if="item.required"> · обязательное</span></p>
              </div>
            </div>
            <span class="merge-attribute__state">
              {{ item.enabled ? 'Показывать в merge' : 'Скрыто из merge' }}
            </span>
          </label>
        </div>
      </div>
    </section>

    <section class="settings-card">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Архивные КА в поиске дублей</h2>
        <p class="settings-card__description">
          Учитывать архивные контрагенты при поиске дубликатов. Ищутся только архивные КА, у которых есть документы для переноса.
        </p>
      </div>

      <div class="settings-card__controls">
        <label class="settings-toggle">
          <input v-model="form.duplicateSearchOptions.includeArchivedWithDocuments" type="checkbox" />
          <span class="settings-toggle__control" />
        </label>
      </div>
    </section>

    <section class="settings-card">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Лимиты поиска</h2>
        <p class="settings-card__description">
          Ограничить количество групп дублей и количество записей внутри группы.
        </p>
      </div>

      <div class="settings-card__controls settings-card__controls--fields">
        <label class="settings-field">
          <span>Максимальные группы</span>
          <input
            v-model.number="form.searchLimits.groupLimit"
            class="settings-input"
            type="number"
            min="1"
            max="1000"
            @change="form.searchLimits.groupLimit = clampLimit(form.searchLimits.groupLimit, defaultSettings.searchLimits.groupLimit)"
          />
        </label>

        <label class="settings-field">
          <span>Максимальная запись в группе</span>
          <input
            v-model.number="form.searchLimits.itemLimit"
            class="settings-input"
            type="number"
            min="1"
            max="1000"
            @change="form.searchLimits.itemLimit = clampLimit(form.searchLimits.itemLimit, defaultSettings.searchLimits.itemLimit)"
          />
        </label>
      </div>
    </section>

    <section class="settings-card">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Автоматическая синхронизация</h2>
        <p class="settings-card__description">
          Настройте расписание автоматической полной синхронизации данных.
        </p>
      </div>

      <div class="settings-card__controls settings-card__controls--inline">
        <label class="settings-toggle">
          <input v-model="form.fullSyncTimer.enabled" type="checkbox" />
          <span class="settings-toggle__control" />
        </label>

        <input v-model="form.fullSyncTimer.runAt" class="settings-input settings-input--time" type="time" />
      </div>
    </section>

    <p v-if="message" class="settings-message">{{ message }}</p>
    <p v-if="saveError" class="settings-error">{{ saveError }}</p>

    <footer class="settings-actions">
      <button class="settings-button settings-button--danger" type="button" @click="resetSettings">
        Сбросить настройки
      </button>

      <div class="settings-actions__right">
        <button class="settings-button settings-button--outline" type="button" @click="cancelChanges">
          Отменить изменения
        </button>

        <button class="settings-button settings-button--primary" type="button" :disabled="saving" @click="saveSettings">
          Сохранить настройки
        </button>
      </div>
    </footer>

    <div v-if="exclusionsOpen" class="settings-modal" @click.self="exclusionsOpen = false">
      <section class="settings-modal__panel" aria-label="Исключения из поиска дублей">
        <header class="settings-modal__header">
          <div>
            <h2>Исключения</h2>
            <p>Управление списком значений, которые не должны попадать в поиск дублей.</p>
          </div>

          <button class="settings-modal__close" type="button" aria-label="Закрыть окно исключений" @click="exclusionsOpen = false">
            ×
          </button>
        </header>

        <div class="settings-modal__body">
          <div class="exclusion-form">
            <select v-model="newExclusion.field" class="settings-select">
              <option v-for="item in exclusionFieldOptions" :key="item.value" :value="item.value">
                {{ item.label }}
              </option>
            </select>

            <input
              v-model="newExclusion.value"
              class="settings-input"
              type="text"
              placeholder="Значение"
              @keydown.enter.prevent="addExclusion"
            />

            <button class="settings-button settings-button--primary" type="button" @click="addExclusion">
              Добавить
            </button>
          </div>

          <div class="exclusion-list exclusion-list--modal">
            <div v-if="!hasExclusions" class="exclusion-item exclusion-item--empty">
              Исключений нет
            </div>

            <div
              v-for="(item, index) in form.duplicateExclusions"
              :key="`${item.field}-${item.value}-${index}`"
              class="exclusion-item"
            >
              <span>{{ exclusionFieldLabel(item.field) }}</span>
              <strong>{{ item.value }}</strong>
              <button type="button" class="exclusion-item__remove" @click="removeExclusion(index)">
                Удалить
              </button>
            </div>
          </div>
        </div>

        <footer class="settings-modal__footer">
          <button class="settings-button settings-button--outline" type="button" @click="exclusionsOpen = false">
            Закрыть
          </button>
        </footer>
      </section>
    </div>
  </main>
</template>

<style>
.settings-page {
  min-height: 100%;
  padding: 24px 32px;
  background: #f4f7fb;
}

.settings-page__header {
  margin-bottom: 22px;
}

.settings-page__title {
  margin: 0;
  color: #0f1b3d;
  font-size: 28px;
  font-weight: 800;
  line-height: 1.2;
}

.settings-page__subtitle {
  margin: 8px 0 0;
  color: #64759b;
  font-size: 15px;
  line-height: 1.4;
}

.settings-card {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(320px, 42%);
  gap: 24px;
  margin-bottom: 16px;
  padding: 22px;
  background: #ffffff;
  border: 1px solid #dfe7f3;
  border-radius: 12px;
  box-shadow: 0 8px 24px rgba(15, 35, 80, 0.06);
}

.settings-card__main {
  min-width: 0;
}

.settings-card__title {
  margin: 0;
  color: #0f1b3d;
  font-size: 20px;
  font-weight: 700;
  line-height: 1.25;
}

.settings-card__description {
  max-width: 720px;
  margin: 8px 0 0;
  color: #64759b;
  font-size: 14px;
  line-height: 1.45;
}

.settings-card__controls {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 12px;
  justify-self: end;
  width: 100%;
}

.settings-card__controls--fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  align-items: end;
}

.settings-card__controls--inline {
  flex-direction: row;
  align-items: center;
  justify-content: flex-end;
}

.settings-card__note {
  grid-column: 1 / -1;
  margin: 0;
  color: #64759b;
  font-size: 13px;
}

.settings-field {
  display: flex;
  flex-direction: column;
  gap: 7px;
  color: #50648f;
  font-size: 13px;
  font-weight: 600;
}

.settings-toggle {
  display: inline-flex;
  align-items: center;
  cursor: pointer;
}

.settings-toggle input {
  position: absolute;
  width: 1px;
  height: 1px;
  opacity: 0;
}

.settings-toggle__control {
  position: relative;
  display: inline-block;
  width: 48px;
  height: 28px;
  background: #c9d5e7;
  border-radius: 999px;
  transition: background 0.2s ease;
}

.settings-toggle__control::after {
  position: absolute;
  top: 4px;
  left: 4px;
  width: 20px;
  height: 20px;
  content: '';
  background: #ffffff;
  border-radius: 50%;
  box-shadow: 0 2px 6px rgba(15, 35, 80, 0.18);
  transition: transform 0.2s ease;
}

.settings-toggle input:checked + .settings-toggle__control {
  background: #2d6cdf;
}

.settings-toggle input:checked + .settings-toggle__control::after {
  transform: translateX(20px);
}

.settings-input,
.settings-select {
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

.settings-input:focus,
.settings-select:focus {
  border-color: #2d6cdf;
  box-shadow: 0 0 0 3px rgba(45, 108, 223, 0.12);
}

.settings-input--time {
  max-width: 150px;
}

.settings-button {
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

.settings-button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.settings-button--primary {
  color: #ffffff;
  background: #2d6cdf;
  border-color: #2d6cdf;
}

.settings-button--outline {
  color: #2d6cdf;
  background: #ffffff;
  border-color: #9bb8ef;
}

.settings-button--danger {
  color: #d92d3f;
  background: #ffffff;
  border-color: #f2a4ad;
}

.settings-modal {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  background: rgba(9, 18, 38, 0.42);
  backdrop-filter: blur(6px);
}

.settings-modal__panel {
  display: grid;
  grid-template-rows: auto minmax(0, 1fr) auto;
  gap: 18px;
  width: min(920px, 100%);
  max-height: min(780px, calc(100vh - 48px));
  padding: 22px;
  background: #ffffff;
  border: 1px solid #dce6f4;
  border-radius: 20px;
  box-shadow: 0 28px 54px rgba(15, 23, 42, 0.22);
}

.settings-modal__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.settings-modal__header h2 {
  margin: 0;
  color: #0f1b3d;
  font-size: 24px;
  font-weight: 800;
  line-height: 1.2;
}

.settings-modal__header p {
  margin: 8px 0 0;
  color: #64759b;
  font-size: 14px;
  line-height: 1.5;
}

.settings-modal__close {
  width: 42px;
  height: 42px;
  flex: 0 0 auto;
  color: #52637e;
  font-size: 24px;
  line-height: 1;
  cursor: pointer;
  background: #ffffff;
  border: 1px solid #d7e1f0;
  border-radius: 12px;
}

.settings-modal__body {
  display: grid;
  gap: 16px;
  min-height: 0;
}

.settings-modal__footer {
  display: flex;
  justify-content: flex-end;
}

.settings-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-top: 22px;
  padding-bottom: 8px;
}

.settings-actions__right {
  display: flex;
  gap: 12px;
}

.settings-message,
.settings-error {
  margin: 12px 0;
  font-size: 14px;
  line-height: 1.4;
}

.settings-message {
  color: #1f8f5f;
}

.settings-error {
  color: #d92d3f;
}

.settings-attributes-summary {
  display: grid;
  gap: 4px;
  width: 100%;
  padding: 12px 14px;
  background: #f7f9fd;
  border: 1px solid #dfe7f3;
  border-radius: 10px;
}

.settings-attributes-summary span {
  color: #64759b;
  font-size: 12px;
  font-weight: 700;
}

.settings-attributes-summary strong {
  color: #0f1b3d;
  font-size: 22px;
  font-weight: 800;
}

.merge-attributes-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  width: 100%;
}

.merge-attribute {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  width: 100%;
  padding: 12px 14px;
  background: #f7f9fd;
  border: 1px solid #dfe7f3;
  border-radius: 10px;
  cursor: pointer;
}

.merge-attribute--empty {
  color: #64759b;
  cursor: default;
}

.merge-attribute__main {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.merge-attribute__main input {
  width: 16px;
  height: 16px;
  margin-top: 2px;
  accent-color: #2d6cdf;
}

.merge-attribute__main strong {
  display: block;
  color: #0f1b3d;
  font-size: 14px;
}

.merge-attribute__main p {
  margin: 4px 0 0;
  color: #64759b;
  font-size: 12px;
  line-height: 1.4;
}

.merge-attribute__state {
  color: #50648f;
  font-size: 12px;
  font-weight: 700;
  text-align: right;
}

.exclusion-form {
  display: grid;
  grid-template-columns: 130px minmax(0, 1fr) auto;
  gap: 10px;
  width: 100%;
}

.exclusion-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: 100%;
}

.exclusion-list--modal {
  min-height: 0;
  overflow: auto;
  padding-right: 4px;
}

.exclusion-item {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  gap: 10px;
  align-items: center;
  min-height: 38px;
  padding: 8px 10px;
  color: #0f1b3d;
  font-size: 14px;
  background: #f7f9fd;
  border: 1px solid #dfe7f3;
  border-radius: 8px;
}

.exclusion-item span {
  color: #64759b;
}

.exclusion-item strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.exclusion-item--empty {
  display: block;
  color: #64759b;
}

.exclusion-item__remove {
  padding: 0;
  color: #d92d3f;
  font: inherit;
  font-weight: 700;
  cursor: pointer;
  background: transparent;
  border: 0;
}

@media (max-width: 900px) {
  .settings-page {
    padding: 20px;
  }

  .settings-card {
    grid-template-columns: 1fr;
  }

  .settings-card__controls {
    justify-self: stretch;
  }

  .settings-card__controls--inline {
    justify-content: flex-start;
  }

  .settings-modal {
    padding: 16px;
  }

  .settings-modal__panel {
    max-height: calc(100vh - 32px);
    padding: 18px;
  }
}

@media (max-width: 620px) {
  .settings-card__controls--fields,
  .exclusion-form {
    grid-template-columns: 1fr;
  }

  .settings-actions,
  .settings-actions__right,
  .merge-attribute {
    flex-direction: column;
    align-items: stretch;
  }

  .settings-button {
    width: 100%;
  }

  .settings-modal__header,
  .settings-modal__footer {
    flex-direction: column;
    align-items: stretch;
  }

  .settings-modal__close {
    width: 100%;
  }

  .exclusion-item {
    grid-template-columns: 1fr;
  }

  .merge-attribute__state {
    text-align: left;
  }
}
</style>
