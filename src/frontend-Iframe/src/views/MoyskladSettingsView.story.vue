<script setup>
import MoyskladSettingsView from './MoyskladSettingsView.vue'
import { buildSettings } from '../mocks/fixtures/sections'

const normal = buildSettings('normal')
const longStrings = buildSettings('long')
const empty = buildSettings('empty')
</script>

<template>
  <Story title="МойСклад/Настройки">
    <Variant title="Настройки">
      <div class="story-page"><MoyskladSettingsView :settings-data="normal" /></div>
    </Variant>
    <Variant title="Длинные исключения">
      <div class="story-page"><MoyskladSettingsView :settings-data="longStrings" /></div>
    </Variant>
    <Variant title="Без исключений и доп. полей">
      <div class="story-page"><MoyskladSettingsView :settings-data="empty" /></div>
    </Variant>
    <Variant title="Загрузка">
      <div class="story-page"><MoyskladSettingsView loading /></div>
    </Variant>
    <Variant title="Ошибка загрузки">
      <div class="story-page"><MoyskladSettingsView load-error="Сервис временно недоступен" /></div>
    </Variant>
    <Variant title="Источник данных не подключён">
      <div class="story-page"><MoyskladSettingsView /></div>
    </Variant>
  </Story>
</template>

<style src="../styles/stories/story-page.css"></style>
