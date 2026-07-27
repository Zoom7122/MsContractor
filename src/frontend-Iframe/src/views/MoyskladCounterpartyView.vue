<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { getCounterpartyPage, runCounterpartyFullSync } from '../api/counterparties'

const route = useRoute()
const router = useRouter()

const loading = ref(false)
const syncing = ref(false)
const error = ref(null)
const syncMessage = ref(null)
const counterparty = ref(createEmptyCounterpartyPage())

const counterpartyId = computed(() => String(route.params.id || '').trim())
const details = computed(() => counterparty.value.item || createEmptyCounterpartyDetails())
const linkedDocumentsPreview = computed(() => counterparty.value.linkedDocuments.slice(0, 8))
const backQuery = computed(() => {
  const query = { ...route.query }
  delete query.returnTo
  return query
})

watch(
  () => counterpartyId.value,
  () => {
    void loadCounterpartyPage()
  },
  { immediate: true }
)

onMounted(() => {
  syncMessage.value = null
})

async function loadCounterpartyPage() {
  if (!counterpartyId.value) {
    error.value = 'Не указан идентификатор контрагента'
    counterparty.value = createEmptyCounterpartyPage()
    return
  }

  loading.value = true
  error.value = null

  try {
    const response = await getCounterpartyPage(counterpartyId.value)
    counterparty.value = normalizeCounterpartyPage(response)
  } catch (requestError) {
    error.value = requestError.message || 'Не удалось загрузить карточку контрагента'
    counterparty.value = createEmptyCounterpartyPage()
  } finally {
    loading.value = false
  }
}

async function handleFullSync() {
  if (!counterpartyId.value || syncing.value) {
    return
  }

  syncing.value = true
  error.value = null
  syncMessage.value = null

  try {
    const response = await runCounterpartyFullSync(counterpartyId.value)
    syncMessage.value = response?.message || 'Полная выгрузка обновлена'
    counterparty.value = normalizeCounterpartyPage(response)
  } catch (requestError) {
    error.value = requestError.message || 'Не удалось обновить полную выгрузку'
  } finally {
    syncing.value = false
  }
}

function handleBack() {
  if (route.query.returnTo === 'history-counterparties') {
    router.push({
      name: 'moysklad-history-counterparties',
      query: backQuery.value
    })
    return
  }

  router.back()
}

function createEmptyCounterpartyDetails() {
  return {
    id: '',
    name: '',
    description: '',
    email: '',
    phone: '',
    archived: false,
    createdAt: '',
    updatedAt: '',
    syncedAt: ''
  }
}

function createEmptyCounterpartyPage() {
  return {
    item: createEmptyCounterpartyDetails(),
    rawJson: '{}',
    latestFullExport: null,
    linkedDocuments: [],
    linkedDocumentsTotal: 0
  }
}

function normalizeCounterpartyPage(source) {
  const detailsSource = source?.item || {}
  return {
    item: {
      id: String(detailsSource?.id || ''),
      name: String(detailsSource?.name || ''),
      description: String(detailsSource?.description || ''),
      email: String(detailsSource?.email || ''),
      phone: String(detailsSource?.phone || ''),
      archived: Boolean(detailsSource?.archived),
      createdAt: String(detailsSource?.createdAt || ''),
      updatedAt: String(detailsSource?.updatedAt || ''),
      syncedAt: String(detailsSource?.syncedAt || '')
    },
    rawJson: String(source?.rawJson || '{}'),
    latestFullExport: source?.latestFullExport
      ? {
          id: Number(source.latestFullExport.id || 0),
          payloadJson: String(source.latestFullExport.payloadJson || ''),
          isPartial: Boolean(source.latestFullExport.isPartial),
          errorCount: Number(source.latestFullExport.errorCount || 0),
          createdAt: String(source.latestFullExport.createdAt || '')
        }
      : null,
    linkedDocuments: Array.isArray(source?.linkedDocuments)
      ? source.linkedDocuments.map((item) => ({
          id: Number(item?.id || 0),
          counterpartyId: String(item?.counterpartyId || ''),
          documentType: String(item?.documentType || ''),
          documentId: String(item?.documentId || ''),
          documentHref: String(item?.documentHref || ''),
          payloadJson: String(item?.payloadJson || ''),
          createdAt: String(item?.createdAt || '')
        }))
      : [],
    linkedDocumentsTotal: Number(source?.linkedDocumentsTotal || 0)
  }
}

function displayValue(value, fallback = '—') {
  return value ? value : fallback
}

function archiveLabel(archived) {
  return archived ? 'Архивный' : 'Активный'
}
</script>

<template>
  <main class="counterparty-page">
    <header class="counterparty-page__header">
      <div>
        <span class="counterparty-page__kicker">Карточка КА</span>
        <h1>{{ displayValue(details.name, 'Контрагент') }}</h1>
        <p>{{ displayValue(details.description, 'Описание отсутствует') }}</p>
      </div>

      <div class="counterparty-page__header-actions">
        <span class="counterparty-page__status" :class="{ 'counterparty-page__status--archived': details.archived }">
          {{ archiveLabel(details.archived) }}
        </span>
        <button class="counterparty-page__button counterparty-page__button--ghost" type="button" @click="handleBack">
          Назад
        </button>
        <button class="counterparty-page__button counterparty-page__button--primary" type="button" :disabled="syncing" @click="handleFullSync">
          {{ syncing ? 'Обновляем...' : 'Обновить полную выгрузку' }}
        </button>
      </div>
    </header>

    <p v-if="loading" class="counterparty-page__message">Загружаем карточку контрагента...</p>
    <p v-if="error" class="counterparty-page__error">{{ error }}</p>
    <p v-if="syncMessage" class="counterparty-page__success">{{ syncMessage }}</p>

    <section v-if="!loading && details.id" class="counterparty-page__grid">
      <section class="counterparty-card">
        <div class="counterparty-card__header">
          <h2>Основные данные</h2>
          <p>Поля карточки и метаданные синхронизации</p>
        </div>

        <dl class="counterparty-card__details">
          <div>
            <dt>ID</dt>
            <dd>{{ displayValue(details.id) }}</dd>
          </div>
          <div>
            <dt>Email</dt>
            <dd>{{ displayValue(details.email) }}</dd>
          </div>
          <div>
            <dt>Телефон</dt>
            <dd>{{ displayValue(details.phone) }}</dd>
          </div>
          <div>
            <dt>Создан</dt>
            <dd>{{ displayValue(details.createdAt) }}</dd>
          </div>
          <div>
            <dt>Обновлён</dt>
            <dd>{{ displayValue(details.updatedAt) }}</dd>
          </div>
          <div>
            <dt>Синхронизирован</dt>
            <dd>{{ displayValue(details.syncedAt) }}</dd>
          </div>
        </dl>
      </section>

      <section class="counterparty-card">
        <div class="counterparty-card__header">
          <h2>Полная выгрузка</h2>
          <p>Последний экспорт и связанные документы</p>
        </div>

        <div class="counterparty-kpis">
          <div class="counterparty-kpi">
            <span>Связанных документов</span>
            <strong>{{ counterparty.linkedDocumentsTotal }}</strong>
          </div>
          <div class="counterparty-kpi">
            <span>Документов в превью</span>
            <strong>{{ linkedDocumentsPreview.length }}</strong>
          </div>
          <div class="counterparty-kpi">
            <span>Ошибок выгрузки</span>
            <strong>{{ counterparty.latestFullExport?.errorCount || 0 }}</strong>
          </div>
        </div>

        <div v-if="counterparty.latestFullExport" class="counterparty-export">
          <div>
            <span>Export ID</span>
            <strong>#{{ counterparty.latestFullExport.id }}</strong>
          </div>
          <div>
            <span>Создан</span>
            <strong>{{ displayValue(counterparty.latestFullExport.createdAt) }}</strong>
          </div>
          <div>
            <span>Статус</span>
            <strong>{{ counterparty.latestFullExport.isPartial ? 'Частичный' : 'Полный' }}</strong>
          </div>
        </div>
      </section>
    </section>

    <section v-if="!loading && details.id" class="counterparty-card counterparty-card--documents">
      <div class="counterparty-card__header">
        <h2>Связанные документы</h2>
        <p>Первые документы из карточки контрагента</p>
      </div>

      <div v-if="!linkedDocumentsPreview.length" class="counterparty-card__empty">
        Связанные документы пока не найдены.
      </div>

      <div v-else class="counterparty-documents">
        <article v-for="document in linkedDocumentsPreview" :key="document.id" class="counterparty-document">
          <div class="counterparty-document__top">
            <strong>{{ displayValue(document.documentType) }}</strong>
            <span>{{ displayValue(document.createdAt) }}</span>
          </div>
          <p>{{ displayValue(document.documentId) }}</p>
        </article>
      </div>
    </section>
  </main>
</template>

<style scoped>
.counterparty-page {
  min-height: 100%;
  padding: 24px 32px 32px;
  background:
    radial-gradient(circle at top left, rgba(45, 108, 223, 0.16), transparent 28%),
    #f4f7fb;
}

.counterparty-page__header {
  display: flex;
  justify-content: space-between;
  gap: 20px;
  margin-bottom: 18px;
  padding: 24px 26px;
  background: linear-gradient(135deg, #08162d, #2351a0);
  border-radius: 18px;
  box-shadow: 0 18px 36px rgba(15, 35, 80, 0.18);
}

.counterparty-page__kicker {
  display: inline-flex;
  margin-bottom: 8px;
  color: #dce8ff;
  font-size: 12px;
  font-weight: 800;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.counterparty-page__header h1 {
  margin: 0;
  color: #ffffff;
  font-size: 30px;
  font-weight: 800;
  line-height: 1.2;
}

.counterparty-page__header p {
  margin: 8px 0 0;
  max-width: 760px;
  color: rgba(227, 235, 255, 0.88);
  font-size: 15px;
  line-height: 1.5;
  white-space: pre-wrap;
}

.counterparty-page__header-actions {
  display: flex;
  align-items: flex-start;
  flex-wrap: wrap;
  gap: 10px;
}

.counterparty-page__status {
  display: inline-flex;
  min-height: 34px;
  align-items: center;
  padding: 0 14px;
  color: #0f1b3d;
  background: #e5f7ee;
  border-radius: 999px;
  font-size: 13px;
  font-weight: 800;
}

.counterparty-page__status--archived {
  color: #9b5d00;
  background: #fff1d6;
}

.counterparty-page__button {
  min-height: 40px;
  padding: 0 16px;
  border: 1px solid transparent;
  border-radius: 12px;
  font-size: 14px;
  font-weight: 800;
  cursor: pointer;
}

.counterparty-page__button:disabled {
  opacity: 0.65;
  cursor: not-allowed;
}

.counterparty-page__button--ghost {
  color: #ffffff;
  background: rgba(255, 255, 255, 0.08);
  border-color: rgba(255, 255, 255, 0.16);
}

.counterparty-page__button--primary {
  color: #0f1b3d;
  background: #ffffff;
  border-color: #ffffff;
}

.counterparty-page__message,
.counterparty-page__error,
.counterparty-page__success {
  margin: 0 0 16px;
  font-size: 14px;
  line-height: 1.4;
}

.counterparty-page__error {
  color: #d92d3f;
}

.counterparty-page__success {
  color: #157347;
}

.counterparty-page__grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
  margin-bottom: 16px;
}

.counterparty-card {
  padding: 22px;
  background: #ffffff;
  border: 1px solid #dfe7f3;
  border-radius: 14px;
  box-shadow: 0 8px 24px rgba(15, 35, 80, 0.06);
}

.counterparty-card__header {
  margin-bottom: 16px;
}

.counterparty-card__header h2 {
  margin: 0;
  color: #0f1b3d;
  font-size: 20px;
  font-weight: 800;
}

.counterparty-card__header p {
  margin: 6px 0 0;
  color: #64759b;
  font-size: 14px;
}

.counterparty-card__details {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
  margin: 0;
}

.counterparty-card__details div {
  display: grid;
  gap: 4px;
}

.counterparty-card__details dt {
  color: #64759b;
  font-size: 12px;
  font-weight: 700;
}

.counterparty-card__details dd {
  margin: 0;
  color: #0f1b3d;
  font-size: 14px;
  line-height: 1.45;
}

.counterparty-kpis {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}

.counterparty-kpi {
  display: grid;
  gap: 6px;
  padding: 14px;
  background: #f8fbff;
  border: 1px solid #d8e4f5;
  border-radius: 12px;
}

.counterparty-kpi span {
  color: #64759b;
  font-size: 12px;
  font-weight: 700;
}

.counterparty-kpi strong {
  color: #0f1b3d;
  font-size: 22px;
  font-weight: 800;
}

.counterparty-export {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
  margin-top: 16px;
}

.counterparty-export div {
  display: grid;
  gap: 4px;
  padding: 14px;
  background: linear-gradient(180deg, #ffffff, #f7faff);
  border: 1px solid #dfe7f3;
  border-radius: 12px;
}

.counterparty-export span {
  color: #64759b;
  font-size: 12px;
  font-weight: 700;
}

.counterparty-export strong {
  color: #0f1b3d;
  font-size: 14px;
  font-weight: 800;
}

.counterparty-card__empty {
  padding: 16px;
  color: #64759b;
  background: #f8fafd;
  border: 1px dashed #d9e5f8;
  border-radius: 12px;
}

.counterparty-documents {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 12px;
}

.counterparty-document {
  display: grid;
  gap: 8px;
  padding: 16px;
  background: linear-gradient(180deg, #ffffff, #f9fbff);
  border: 1px solid #dfe7f3;
  border-radius: 12px;
}

.counterparty-document__top {
  display: flex;
  justify-content: space-between;
  gap: 10px;
}

.counterparty-document__top strong {
  color: #0f1b3d;
  font-size: 14px;
}

.counterparty-document__top span,
.counterparty-document p {
  margin: 0;
  color: #64759b;
  font-size: 13px;
  line-height: 1.4;
}

@media (max-width: 920px) {
  .counterparty-page__header,
  .counterparty-page__header-actions {
    flex-direction: column;
    align-items: stretch;
  }

  .counterparty-page__grid,
  .counterparty-kpis,
  .counterparty-export,
  .counterparty-card__details {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 640px) {
  .counterparty-page {
    padding: 20px;
  }
}
</style>
