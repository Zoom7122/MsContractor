<script setup>
import { computed, onMounted, ref } from 'vue'
import { Close, Plus } from '@element-plus/icons-vue'

import { getCatalogSettings, saveCatalogSettings } from '../api/catalog'
import EmptyState from '../components/ui/EmptyState.vue'
import ErrorNotice from '../components/ui/ErrorNotice.vue'
import PageHeader from '../components/ui/PageHeader.vue'
import SectionPanel from '../components/ui/SectionPanel.vue'
import StatusBadge from '../components/ui/StatusBadge.vue'
import { matchFieldMeta } from '../domain/duplicates'

const defaultSettings = {
  duplicateExclusions: [],
  duplicateSearchOptions: {
    includeArchivedWithDocuments: true
  },
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
const loading = ref(true)
const loadError = ref(null)
const saving = ref(false)
const message = ref(null)
const saveError = ref(null)
const exclusionsOpen = ref(false)
const newExclusion = ref({
  field: 'email',
  value: ''
})

const hasExclusions = computed(() => form.value.duplicateExclusions.length > 0)
const isDirty = computed(() =>
  JSON.stringify(toCatalogSettings(form.value)) !== JSON.stringify(toCatalogSettings(initialSettings.value))
)
const exclusionsPreview = computed(() => form.value.duplicateExclusions.slice(0, 4))

onMounted(loadSettings)

async function loadSettings() {
  loading.value = true
  loadError.value = null

  try {
    const settings = await getCatalogSettings()
    const normalized = normalizeSettings(settings)
    initialSettings.value = cloneSettings(normalized)
    form.value = cloneSettings(normalized)
  } catch (error) {
    loadError.value = error
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

function cloneSettings(value) {
  return JSON.parse(JSON.stringify(value))
}

function toCatalogSettings(source) {
  return {
    duplicateExclusions: source.duplicateExclusions.map(({ field, value }) => ({ field, value })),
    duplicateSearchOptions: {
      includeArchivedWithDocuments: Boolean(source.duplicateSearchOptions.includeArchivedWithDocuments)
    },
    searchLimits: {
      groupLimit: clampLimit(source.searchLimits.groupLimit, defaultSettings.searchLimits.groupLimit),
      itemLimit: clampLimit(source.searchLimits.itemLimit, defaultSettings.searchLimits.itemLimit)
    }
  }
}

function normalizeExclusionField(value) {
  const normalized = String(value || '').trim()
  return exclusionFieldOptions.some((item) => item.value === normalized) ? normalized : ''
}

function clampLimit(value, fallback) {
  const numeric = Number(value)
  if (!Number.isFinite(numeric)) {
    return fallback
  }

  return Math.min(1000, Math.max(1, Math.trunc(numeric)))
}

function exclusionFieldIcon(field) {
  return matchFieldMeta(field).icon
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

function resetSettings() {
  form.value = {
    duplicateExclusions: [],
    duplicateSearchOptions: cloneSettings(defaultSettings.duplicateSearchOptions),
    searchLimits: cloneSettings(defaultSettings.searchLimits)
  }
  message.value = null
  saveError.value = null
}

function cancelChanges() {
  form.value = cloneSettings(initialSettings.value)
  message.value = null
  saveError.value = null
}

async function saveSettings() {
  message.value = null
  saveError.value = null
  saving.value = true

  const settings = toCatalogSettings(form.value)
  try {
    await saveCatalogSettings(settings)
    initialSettings.value = {
      ...initialSettings.value,
      duplicateExclusions: cloneSettings(settings.duplicateExclusions),
      duplicateSearchOptions: cloneSettings(settings.duplicateSearchOptions),
      searchLimits: cloneSettings(settings.searchLimits)
    }
    message.value = 'Настройки поиска сохранены. Они пока не меняют выдачу поиска.'
  } catch (error) {
    saveError.value = error
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="app-page settings-page">
    <PageHeader title="Настройки" subtitle="Настройки поиска дублей">
      <template v-if="isDirty" #meta>
        <StatusBadge tone="warning" label="Есть несохранённые изменения" />
      </template>
    </PageHeader>

    <ErrorNotice
      v-if="loadError"
      :error="loadError"
      fallback="Не удалось загрузить настройки"
      description="Показаны значения по умолчанию. Проверьте их перед сохранением."
    />
    <el-skeleton v-if="loading" :rows="8" animated class="settings-skeleton" />

    <template v-else>
      <SectionPanel title="Поиск дублей" subtitle="Исключения, архивные контрагенты и лимиты выдачи" flush>
        <div class="settings-row">
          <div class="settings-row__text">
            <h3 class="settings-row__title">Исключения</h3>
            <p class="settings-row__description">
              Значения, которые не должны склеивать контрагентов в дубли: общие email, телефоны call-центра,
              «Розничный покупатель».
            </p>
          </div>
          <div class="settings-row__control settings-row__control--stack">
            <ul v-if="hasExclusions" class="settings-chips">
              <li v-for="(item, index) in exclusionsPreview" :key="`${item.field}-${index}`" class="settings-chip">
                <el-icon aria-hidden="true"><component :is="exclusionFieldIcon(item.field)" /></el-icon>
                <span class="settings-chip__value">{{ item.value }}</span>
              </li>
              <li v-if="form.duplicateExclusions.length > exclusionsPreview.length" class="settings-chip settings-chip--more">
                ещё {{ form.duplicateExclusions.length - exclusionsPreview.length }}
              </li>
            </ul>
            <span v-else class="app-meta">Исключений нет</span>
            <el-button size="small" @click="exclusionsOpen = true">
              {{ hasExclusions ? `Изменить (${form.duplicateExclusions.length})` : 'Добавить исключения' }}
            </el-button>
          </div>
        </div>

        <div class="settings-row">
          <div class="settings-row__text">
            <h3 class="settings-row__title">Архивные контрагенты</h3>
            <p class="settings-row__description">
              Учитывать архивных контрагентов, у которых есть документы: их документы тоже перейдут к основному.
            </p>
          </div>
          <div class="settings-row__control">
            <el-switch
              v-model="form.duplicateSearchOptions.includeArchivedWithDocuments"
              aria-label="Учитывать архивных контрагентов с документами"
            />
          </div>
        </div>

        <div class="settings-row">
          <div class="settings-row__text">
            <h3 class="settings-row__title">Лимиты поиска</h3>
            <p class="settings-row__description">
              Ограничивают объём выдачи на больших базах. Значения от 1 до 1000.
            </p>
          </div>
          <el-form class="settings-row__control settings-limits" label-position="top" size="small">
            <el-form-item label="Групп дублей, не более">
              <el-input-number
                v-model="form.searchLimits.groupLimit"
                :min="1"
                :max="1000"
                controls-position="right"
                @change="form.searchLimits.groupLimit = clampLimit(form.searchLimits.groupLimit, defaultSettings.searchLimits.groupLimit)"
              />
            </el-form-item>
            <el-form-item label="Контрагентов в группе, не более">
              <el-input-number
                v-model="form.searchLimits.itemLimit"
                :min="1"
                :max="1000"
                controls-position="right"
                @change="form.searchLimits.itemLimit = clampLimit(form.searchLimits.itemLimit, defaultSettings.searchLimits.itemLimit)"
              />
            </el-form-item>
          </el-form>
        </div>
      </SectionPanel>

    </template>

    <el-alert v-if="message" :title="message" type="success" :closable="false" show-icon />
    <ErrorNotice v-if="saveError" :error="saveError" />

    <footer class="settings-actions">
      <el-button text type="danger" @click="resetSettings">Сбросить к умолчаниям</el-button>
      <div class="settings-actions__right">
        <el-button :disabled="!isDirty || saving" @click="cancelChanges">Отменить изменения</el-button>
        <el-button
          type="primary"
          :loading="saving"
          :disabled="loading || saving"
          @click="saveSettings"
        >Сохранить</el-button>
      </div>
    </footer>

    <el-dialog
      v-model="exclusionsOpen"
      class="settings-modal"
      title="Исключения из поиска дублей"
      width="min(640px, calc(100vw - 32px))"
      :close-on-press-escape="false"
      append-to-body
    >
      <p class="settings-modal__description">
        Контрагенты с этими значениями не будут объединяться в группы дублей по соответствующему полю.
      </p>

      <el-form class="exclusion-form" size="default" @submit.prevent="addExclusion">
        <el-select v-model="newExclusion.field" class="exclusion-form__field" aria-label="Поле исключения">
          <el-option
            v-for="item in exclusionFieldOptions"
            :key="item.value"
            :label="item.label"
            :value="item.value"
          />
        </el-select>
        <el-input
          v-model="newExclusion.value"
          class="exclusion-form__value"
          placeholder="Значение, например test@example.com"
          @keydown.enter.prevent="addExclusion"
        />
        <el-button type="primary" :icon="Plus" :disabled="!newExclusion.value.trim()" @click="addExclusion">
          Добавить
        </el-button>
      </el-form>

      <EmptyState
        v-if="!hasExclusions"
        size="sm"
        image="search"
        title="Исключений пока нет"
        description="Добавьте значение выше — например, общий email отдела продаж."
      />
      <ul v-else class="exclusion-list">
        <li
          v-for="(item, index) in form.duplicateExclusions"
          :key="`${item.field}-${item.value}-${index}`"
          class="exclusion-item"
        >
          <span class="exclusion-item__field">
            <el-icon aria-hidden="true"><component :is="exclusionFieldIcon(item.field)" /></el-icon>
            {{ exclusionFieldLabel(item.field) }}
          </span>
          <span class="exclusion-item__value">{{ item.value }}</span>
          <el-button
            text
            circle
            size="small"
            :icon="Close"
            :aria-label="`Удалить исключение ${item.value}`"
            @click="removeExclusion(index)"
          />
        </li>
      </ul>

      <template #footer>
        <el-button type="primary" @click="exclusionsOpen = false">Готово</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped src="../styles/pages/settings.css"></style>
