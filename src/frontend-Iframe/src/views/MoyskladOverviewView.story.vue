<script setup>
import MoyskladOverviewView from './MoyskladOverviewView.vue'
import { buildBusyCounterpartyIds, buildMergeJobs } from '../mocks/fixtures/merge'
import { buildOverview } from '../mocks/fixtures/sections'

function variant(scenario) {
  const jobs = buildMergeJobs(scenario)
  return {
    ...buildOverview(scenario, jobs),
    mergeQueueData: { jobs, busyCounterpartyIds: buildBusyCounterpartyIds(jobs) }
  }
}

const normal = variant('normal')
const partial = variant('partial')
const many = variant('many')
const empty = variant('empty')
</script>

<template>
  <Story title="МойСклад/Обзор">
    <Variant title="Обычные данные">
      <div class="story-page"><MoyskladOverviewView v-bind="normal" /></div>
    </Variant>
    <Variant title="Частичные ошибки merge">
      <div class="story-page"><MoyskladOverviewView v-bind="partial" /></div>
    </Variant>
    <Variant title="Много задач">
      <div class="story-page"><MoyskladOverviewView v-bind="many" /></div>
    </Variant>
    <Variant title="Синхронизация не выполнялась">
      <div class="story-page"><MoyskladOverviewView v-bind="empty" /></div>
    </Variant>
    <Variant title="Загрузка">
      <div class="story-page"><MoyskladOverviewView loading :auto-load="false" /></div>
    </Variant>
    <Variant title="Ошибка">
      <div class="story-page"><MoyskladOverviewView load-error="Сервис временно недоступен" :auto-load="false" /></div>
    </Variant>
    <Variant title="Источник данных не подключён">
      <div class="story-page"><MoyskladOverviewView :auto-load="false" /></div>
    </Variant>
  </Story>
</template>

<style src="../styles/stories/story-page.css"></style>
