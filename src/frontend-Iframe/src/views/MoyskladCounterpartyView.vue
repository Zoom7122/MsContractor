<script setup>
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'

const route = useRoute()
const router = useRouter()

const loading = ref(false)
const syncing = ref(false)
const error = ref('Раздел временно недоступен')
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

function handleFullSync() {
  syncMessage.value = null
  error.value = 'Раздел временно недоступен'
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
        <el-tag :type="details.archived ? 'info' : 'success'" effect="light">
          {{ archiveLabel(details.archived) }}
        </el-tag>
        <el-button plain @click="handleBack">Назад</el-button>
        <el-button type="primary" :loading="syncing" disabled>Обновить полную выгрузку</el-button>
      </div>
    </header>

    <el-skeleton v-if="loading" :rows="4" animated />
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-alert v-if="syncMessage" :title="syncMessage" type="success" :closable="false" show-icon />

    <section v-if="!loading && details.id" class="counterparty-page__grid">
      <el-card class="counterparty-card" shadow="never">
        <template #header>
          <div class="counterparty-card__header">
            <h2>Основные данные</h2>
            <p>Поля карточки и метаданные синхронизации</p>
          </div>
        </template>
        <el-descriptions :column="2" border size="small">
          <el-descriptions-item label="ID">{{ displayValue(details.id) }}</el-descriptions-item>
          <el-descriptions-item label="Email">{{ displayValue(details.email) }}</el-descriptions-item>
          <el-descriptions-item label="Телефон">{{ displayValue(details.phone) }}</el-descriptions-item>
          <el-descriptions-item label="Создан">{{ displayValue(details.createdAt) }}</el-descriptions-item>
          <el-descriptions-item label="Обновлён">{{ displayValue(details.updatedAt) }}</el-descriptions-item>
          <el-descriptions-item label="Синхронизирован">{{ displayValue(details.syncedAt) }}</el-descriptions-item>
        </el-descriptions>
      </el-card>

      <el-card class="counterparty-card" shadow="never">
        <template #header>
          <div class="counterparty-card__header">
            <h2>Полная выгрузка</h2>
            <p>Последний экспорт и связанные документы</p>
          </div>
        </template>
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

        <el-descriptions v-if="counterparty.latestFullExport" :column="3" border size="small">
          <el-descriptions-item label="Export ID">#{{ counterparty.latestFullExport.id }}</el-descriptions-item>
          <el-descriptions-item label="Создан">{{ displayValue(counterparty.latestFullExport.createdAt) }}</el-descriptions-item>
          <el-descriptions-item label="Статус">
            <el-tag :type="counterparty.latestFullExport.isPartial ? 'warning' : 'success'" size="small">
              {{ counterparty.latestFullExport.isPartial ? 'Частичный' : 'Полный' }}
            </el-tag>
          </el-descriptions-item>
        </el-descriptions>
      </el-card>
    </section>

    <el-card v-if="!loading && details.id" class="counterparty-card counterparty-card--documents" shadow="never">
      <template #header>
        <div class="counterparty-card__header">
          <h2>Связанные документы</h2>
          <p>Первые документы из карточки контрагента</p>
        </div>
      </template>

      <el-empty v-if="!linkedDocumentsPreview.length" description="Связанные документы пока не найдены" :image-size="64" />
      <div v-else class="counterparty-documents">
        <el-card v-for="document in linkedDocumentsPreview" :key="document.id" class="counterparty-document" shadow="never">
          <div class="counterparty-document__top">
            <strong>{{ displayValue(document.documentType) }}</strong>
            <span>{{ displayValue(document.createdAt) }}</span>
          </div>
          <p>{{ displayValue(document.documentId) }}</p>
        </el-card>
      </div>
    </el-card>
  </main>
</template>

<style scoped src="../styles/pages/counterparty.css"></style>
