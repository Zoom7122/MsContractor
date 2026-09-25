<script setup>
import MoyskladDuplicatesView from './MoyskladDuplicatesView.vue'
import { buildDuplicateGroups } from '../mocks/fixtures/counterparties'

// API shape of POST /api/merge-preview: [{ matchedBy, matchValue, counterparties }]
const normal = buildDuplicateGroups('normal')
const criteria = buildDuplicateGroups('criteria')
const longStrings = buildDuplicateGroups('long')
const many = buildDuplicateGroups('many')
const empty = []
</script>

<template>
  <Story title="МойСклад/Дубликаты">
    <Variant title="Список дублей">
      <div class="story-page"><MoyskladDuplicatesView :duplicates-data="normal" /></div>
    </Variant>
    <Variant title="Много критериев совпадения">
      <div class="story-page"><MoyskladDuplicatesView :duplicates-data="criteria" /></div>
    </Variant>
    <Variant title="Длинные строки">
      <div class="story-page"><MoyskladDuplicatesView :duplicates-data="longStrings" /></div>
    </Variant>
    <Variant title="Много групп">
      <div class="story-page"><MoyskladDuplicatesView :duplicates-data="many" /></div>
    </Variant>
    <Variant title="Дублей не найдено">
      <div class="story-page"><MoyskladDuplicatesView :duplicates-data="empty" /></div>
    </Variant>
  </Story>
</template>

<style src="../styles/stories/story-page.css"></style>
