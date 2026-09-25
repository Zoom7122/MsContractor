<script setup>
import MoyskladHistoryView from './MoyskladHistoryView.vue'
import { buildHistory } from '../mocks/fixtures/sections'

const normal = buildHistory('normal')
const longStrings = buildHistory('long')
const many = buildHistory('many')
const empty = buildHistory('empty')
</script>

<template>
  <Story title="МойСклад/История изменений">
    <Variant title="Записи">
      <div class="story-page"><MoyskladHistoryView :history-data="normal" /></div>
    </Variant>
    <Variant title="Длинные значения">
      <div class="story-page"><MoyskladHistoryView :history-data="longStrings" /></div>
    </Variant>
    <Variant title="Много записей">
      <div class="story-page"><MoyskladHistoryView :history-data="many" /></div>
    </Variant>
    <Variant title="Пусто">
      <div class="story-page"><MoyskladHistoryView :history-data="empty" /></div>
    </Variant>
    <Variant title="Загрузка">
      <div class="story-page"><MoyskladHistoryView loading /></div>
    </Variant>
    <Variant title="Источник данных не подключён">
      <div class="story-page"><MoyskladHistoryView /></div>
    </Variant>
  </Story>
</template>

<style src="../styles/stories/story-page.css"></style>
