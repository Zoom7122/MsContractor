<script setup>
import { computed, ref, watch } from 'vue'

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
const loadError = ref(props.settingsData ? null : 'Раздел временно недоступен')
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

function saveSettings() {
  message.value = null
  saveError.value = 'Раздел временно недоступен'
}
</script>

<template>
  <main class="settings-page">
    <header class="settings-page__header">
      <h1 class="settings-page__title">Настройки</h1>
      <p class="settings-page__subtitle">Управление подключением, merge-логикой и параметрами работы MS Contractor</p>
    </header>

    <el-skeleton v-if="loading" :rows="4" animated />
    <el-alert v-if="loadError" title="Не удалось загрузить настройки" type="error" :closable="false" show-icon />

    <el-card class="settings-card" shadow="never">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Исключения</h2>
        <p class="settings-card__description">
          Email, телефоны или имена, которые не должны попадать в дубликаты.
        </p>
      </div>
      <div class="settings-card__controls">
        <el-tag type="info" effect="plain">Всего: {{ form.duplicateExclusions.length }}</el-tag>
        <el-button plain @click="exclusionsOpen = true">Открыть исключения</el-button>
      </div>
    </el-card>

    <el-card class="settings-card" shadow="never">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Доп. поля в объединении</h2>
        <p class="settings-card__description">
          Поля контрагентов, которые отображаются при выборе итоговых значений merge.
        </p>
      </div>
      <div class="settings-card__controls">
        <el-tag type="info" effect="plain">Выбрано: {{ enabledMergeAttributesCount }}</el-tag>
        <el-button plain @click="mergeAttributesOpen = !mergeAttributesOpen">
          {{ mergeAttributesOpen ? 'Скрыть доп. поля' : 'Открыть доп. поля' }}
        </el-button>

        <el-collapse-transition>
          <div v-if="mergeAttributesOpen" class="merge-attributes-list">
            <el-empty v-if="!hasMergeAttributes" description="Дополнительные поля не найдены" :image-size="64" />
            <div v-for="item in form.mergeAttributes" :key="item.attributeId" class="merge-attribute">
              <el-checkbox
                :model-value="item.enabled"
                @change="toggleMergeAttribute(item.attributeId)"
              >
                <span class="merge-attribute__main">
                  <strong>{{ item.name }}</strong>
                  <span>{{ item.type || 'unknown' }}<span v-if="item.required"> · обязательное</span></span>
                </span>
              </el-checkbox>
              <el-tag :type="item.enabled ? 'success' : 'info'" size="small" effect="light">
                {{ item.enabled ? 'Показывать в merge' : 'Скрыто из merge' }}
              </el-tag>
            </div>
          </div>
        </el-collapse-transition>
      </div>
    </el-card>

    <el-card class="settings-card" shadow="never">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Архивные КА в поиске дублей</h2>
        <p class="settings-card__description">
          Учитывать архивных контрагентов с документами для переноса.
        </p>
      </div>
      <div class="settings-card__controls">
        <el-switch v-model="form.duplicateSearchOptions.includeArchivedWithDocuments" />
      </div>
    </el-card>

    <el-card class="settings-card" shadow="never">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Лимиты поиска</h2>
        <p class="settings-card__description">
          Максимальное количество групп дублей и записей внутри группы.
        </p>
      </div>
      <el-form class="settings-card__controls settings-card__controls--fields" label-position="top" size="small">
        <el-form-item label="Максимальные группы">
          <el-input-number
            v-model="form.searchLimits.groupLimit"
            :min="1"
            :max="1000"
            controls-position="right"
            @change="form.searchLimits.groupLimit = clampLimit(form.searchLimits.groupLimit, defaultSettings.searchLimits.groupLimit)"
          />
        </el-form-item>
        <el-form-item label="Максимальная запись в группе">
          <el-input-number
            v-model="form.searchLimits.itemLimit"
            :min="1"
            :max="1000"
            controls-position="right"
            @change="form.searchLimits.itemLimit = clampLimit(form.searchLimits.itemLimit, defaultSettings.searchLimits.itemLimit)"
          />
        </el-form-item>
      </el-form>
    </el-card>

    <el-card class="settings-card" shadow="never">
      <div class="settings-card__main">
        <h2 class="settings-card__title">Автоматическая синхронизация</h2>
        <p class="settings-card__description">Расписание автоматической полной синхронизации данных.</p>
      </div>
      <div class="settings-card__controls settings-card__controls--inline">
        <el-switch v-model="form.fullSyncTimer.enabled" />
        <el-time-picker
          v-model="form.fullSyncTimer.runAt"
          value-format="HH:mm"
          format="HH:mm"
          placeholder="Время запуска"
        />
      </div>
    </el-card>

    <el-alert v-if="message" :title="message" type="success" :closable="false" show-icon />
    <el-alert v-if="saveError" :title="saveError" type="error" :closable="false" show-icon />

    <footer class="settings-actions">
      <el-button type="danger" plain @click="resetSettings">Сбросить настройки</el-button>
      <div class="settings-actions__right">
        <el-button plain @click="cancelChanges">Отменить изменения</el-button>
        <el-button type="primary" disabled>Сохранить настройки</el-button>
      </div>
    </footer>

    <el-dialog
      v-model="exclusionsOpen"
      class="settings-modal"
      title="Исключения"
      width="min(680px, calc(100vw - 32px))"
      :close-on-press-escape="false"
    >
      <p class="settings-modal__description">
        Значения, которые не должны попадать в поиск дублей.
      </p>

      <el-form class="exclusion-form" inline size="small" @submit.prevent="addExclusion">
        <el-form-item>
          <el-select v-model="newExclusion.field" aria-label="Поле исключения">
            <el-option
              v-for="item in exclusionFieldOptions"
              :key="item.value"
              :label="item.label"
              :value="item.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item class="exclusion-form__value">
          <el-input
            v-model="newExclusion.value"
            placeholder="Значение"
            @keydown.enter.prevent="addExclusion"
          />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" @click="addExclusion">Добавить</el-button>
        </el-form-item>
      </el-form>

      <el-empty v-if="!hasExclusions" description="Исключений нет" :image-size="64" />
      <div v-else class="exclusion-list">
        <div
          v-for="(item, index) in form.duplicateExclusions"
          :key="`${item.field}-${item.value}-${index}`"
          class="exclusion-item"
        >
          <el-tag type="info" size="small">{{ exclusionFieldLabel(item.field) }}</el-tag>
          <strong>{{ item.value }}</strong>
          <el-button link type="danger" @click="removeExclusion(index)">Удалить</el-button>
        </div>
      </div>

      <template #footer>
        <el-button @click="exclusionsOpen = false">Закрыть</el-button>
      </template>
    </el-dialog>
  </main>
</template>

<style scoped src="../styles/pages/settings.css"></style>
