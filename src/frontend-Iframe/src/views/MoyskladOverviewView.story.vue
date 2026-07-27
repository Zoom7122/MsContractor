<script setup>
import MoyskladOverviewView from './MoyskladOverviewView.vue'

const connectedOverviewData = {
  connection: {
    ok: true,
    label: 'Подключено',
    description: 'Соединение активно',
    counterpartyTotal: 1356,
  },
  local: {
    counterpartiesCount: 978,
  },
  duplicates: {
    groupsCount: 8,
  },
  mergeQueue: {
    jobsCount: 3,
  },
  lastSync: {
    startedAtLabel: '13.06.2026 15:02',
    modeLabel: 'Полная синхронизация',
  },
}

const dashboardStatusData = {
  totalCounterparties: 1356,
  processedCounterparties: 978,
  newCounterparties: 157,
  updatedCounterparties: 784,
  errorsCount: 4,
  progressPercent: 72,
  currentStep: 'Обновление данных контрагентов',
  currentPage: 12,
  totalPages: 18,
  running: true,
  status: 'running',
}

const idleDashboardStatusData = {
  totalCounterparties: 120,
  processedCounterparties: 0,
  newCounterparties: 0,
  updatedCounterparties: 0,
  errorsCount: 0,
  progressPercent: 0,
  currentStep: null,
  currentPage: null,
  totalPages: null,
  running: false,
  status: 'idle',
}

const disconnectedOverviewData = {
  connection: {
    ok: false,
    label: 'Не подключено',
    description: 'Проверьте установку решения',
    counterpartyTotal: 0,
  },
  local: {
    counterpartiesCount: 0,
  },
  duplicates: {
    groupsCount: 0,
  },
  mergeQueue: {
    jobsCount: 0,
  },
  lastSync: {
    startedAtLabel: null,
    modeLabel: 'Синхронизация ещё не запускалась',
  },
}

const emptyOverviewData = {
  connection: {
    ok: null,
    label: null,
    description: null,
    counterpartyTotal: 0,
  },
  local: {
    counterpartiesCount: 0,
  },
  duplicates: {
    groupsCount: 0,
  },
  mergeQueue: {
    jobsCount: 0,
  },
  lastSync: {
    startedAtLabel: null,
    modeLabel: null,
  },
}
</script>

<template>
  <Story title="МойСклад/Обзор">
    <Variant title="Подключено">
      <div class="story-page">
        <MoyskladOverviewView
          :overview-data="connectedOverviewData"
          :dashboard-status-data="dashboardStatusData"
        />
      </div>
    </Variant>

    <Variant title="Нет подключения">
      <div class="story-page">
        <MoyskladOverviewView
          :overview-data="disconnectedOverviewData"
          :dashboard-status-data="idleDashboardStatusData"
        />
      </div>
    </Variant>

    <Variant title="Пустое состояние">
      <div class="story-page">
        <MoyskladOverviewView
          :overview-data="emptyOverviewData"
          :dashboard-status-data="idleDashboardStatusData"
        />
      </div>
    </Variant>
  </Story>
</template>

<style>
.story-page {
  min-height: 100vh;
  padding: 32px;
  background: #f4f7fb;
}
</style>
