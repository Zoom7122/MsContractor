<script setup>
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { RefreshRight } from '@element-plus/icons-vue'

import CopyableId from '../components/ui/CopyableId.vue'
import EmptyState from '../components/ui/EmptyState.vue'
import ErrorNotice from '../components/ui/ErrorNotice.vue'
import PageHeader from '../components/ui/PageHeader.vue'
import SectionPanel from '../components/ui/SectionPanel.vue'
import StatusBadge from '../components/ui/StatusBadge.vue'
import { documentTypeLabel } from '../domain/merge'
import { formatDateTime, formatNumber } from '../utils/format'

const props = defineProps({
  counterpartyData: {
    type: Object,
    default: null
  },
  loading: {
    type: Boolean,
    default: false
  },
  loadError: {
    type: [String, Object],
    default: null
  }
})

const route = useRoute()
const router = useRouter()

const syncing = ref(false)
const syncMessage = ref(null)
const counterparty = computed(() =>
  props.counterpartyData ? normalizeCounterpartyPage(props.counterpartyData) : createEmptyCounterpartyPage()
)
const unavailable = computed(() => !props.counterpartyData && !props.loading && !props.loadError)

const counterpartyId = computed(() => String(route.params.id || '').trim())
const details = computed(() => counterparty.value.item || createEmptyCounterpartyDetails())
const linkedDocumentsPreview = computed(() => counterparty.value.linkedDocuments.slice(0, 8))
const backQuery = computed(() => {
  const query = { ...route.query }
  delete query.returnTo
  return query
})

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
  <div class="app-page counterparty-page">
    <PageHeader
      :title="displayValue(details.name, 'Карточка контрагента')"
      show-back
      back-label="Назад"
      @back="handleBack"
    >
      <template v-if="details.id" #meta>
        <StatusBadge :tone="details.archived ? 'neutral' : 'success'" :label="archiveLabel(details.archived)" />
      </template>
      <template v-if="details.description" #subtitle>
        <span class="counterparty-page__description">{{ details.description }}</span>
      </template>
      <template #actions>
        <el-tooltip content="Обновление выгрузки пока недоступно" placement="top">
          <span>
            <el-button :icon="RefreshRight" :loading="syncing" disabled>Обновить выгрузку</el-button>
          </span>
        </el-tooltip>
      </template>
    </PageHeader>

    <ErrorNotice v-if="loadError" :error="loadError" fallback="Не удалось загрузить карточку контрагента" />
    <el-alert v-if="syncMessage" :title="syncMessage" type="success" :closable="false" show-icon />

    <section v-if="loading" class="counterparty-page__grid" aria-busy="true">
      <SectionPanel v-for="index in 2" :key="index">
        <el-skeleton :rows="4" animated />
      </SectionPanel>
    </section>

    <SectionPanel v-else-if="unavailable">
      <EmptyState
        image="unavailable"
        title="Карточка пока недоступна"
        description="Подробные данные контрагента, выгрузки и связанные документы появятся здесь после подключения сервиса карточек."
      >
        <CopyableId :value="counterpartyId" label="ID контрагента" full />
      </EmptyState>
    </SectionPanel>

    <template v-else-if="details.id">
      <section class="counterparty-page__grid">
        <SectionPanel title="Основные данные" subtitle="Поля карточки и метаданные синхронизации">
          <el-descriptions :column="1" border size="small" class="counterparty-page__descriptions">
            <el-descriptions-item label="Email">{{ displayValue(details.email) }}</el-descriptions-item>
            <el-descriptions-item label="Телефон">
              <span class="app-nums">{{ displayValue(details.phone) }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="Создан">
              <span class="app-nums">{{ formatDateTime(details.createdAt) }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="Обновлён">
              <span class="app-nums">{{ formatDateTime(details.updatedAt) }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="Синхронизирован">
              <span class="app-nums">{{ formatDateTime(details.syncedAt) }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="ID">
              <CopyableId :value="details.id" full />
            </el-descriptions-item>
          </el-descriptions>
        </SectionPanel>

        <SectionPanel title="Полная выгрузка" subtitle="Последний экспорт карточки и связанных документов">
          <template v-if="counterparty.latestFullExport" #actions>
            <StatusBadge
              size="sm"
              :tone="counterparty.latestFullExport.isPartial ? 'warning' : 'success'"
              :label="counterparty.latestFullExport.isPartial ? 'Частичная' : 'Полная'"
            />
          </template>

          <dl class="counterparty-kpis">
            <div class="counterparty-kpi">
              <dt>Связанных документов</dt>
              <dd class="app-nums">{{ formatNumber(counterparty.linkedDocumentsTotal) }}</dd>
            </div>
            <div class="counterparty-kpi">
              <dt>Ошибок выгрузки</dt>
              <dd class="app-nums" :class="{ 'counterparty-kpi__value--danger': counterparty.latestFullExport?.errorCount }">
                {{ formatNumber(counterparty.latestFullExport?.errorCount || 0) }}
              </dd>
            </div>
            <div class="counterparty-kpi">
              <dt>Выгрузка от</dt>
              <dd class="app-nums counterparty-kpi__date">{{ formatDateTime(counterparty.latestFullExport?.createdAt) }}</dd>
            </div>
          </dl>

          <p v-if="counterparty.latestFullExport" class="app-meta counterparty-page__export-id">
            Выгрузка #{{ counterparty.latestFullExport.id }}
          </p>
          <p v-else class="app-meta">Полная выгрузка ещё не выполнялась.</p>
        </SectionPanel>
      </section>

      <SectionPanel title="Связанные документы" flush>
        <template #actions>
          <span v-if="counterparty.linkedDocumentsTotal > linkedDocumentsPreview.length" class="app-meta app-nums">
            Показаны первые {{ linkedDocumentsPreview.length }} из {{ formatNumber(counterparty.linkedDocumentsTotal) }}
          </span>
        </template>

        <EmptyState
          v-if="!linkedDocumentsPreview.length"
          size="sm"
          image="documents"
          title="Связанных документов нет"
          description="У контрагента нет документов в последней выгрузке."
        />
        <el-table v-else :data="linkedDocumentsPreview" table-layout="fixed" row-key="id">
          <el-table-column label="Тип документа" min-width="200">
            <template #default="{ row }">{{ documentTypeLabel(row.documentType) }}</template>
          </el-table-column>
          <el-table-column label="ID документа" min-width="220">
            <template #default="{ row }">
              <CopyableId :value="row.documentId" full />
            </template>
          </el-table-column>
          <el-table-column label="Дата" width="160">
            <template #default="{ row }">
              <span class="app-text-secondary app-nums">{{ formatDateTime(row.createdAt) }}</span>
            </template>
          </el-table-column>
        </el-table>
      </SectionPanel>
    </template>
  </div>
</template>

<style scoped src="../styles/pages/counterparty.css"></style>
