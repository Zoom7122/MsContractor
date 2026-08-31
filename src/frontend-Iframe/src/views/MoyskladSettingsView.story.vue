<script setup>
import MoyskladSettingsView from './MoyskladSettingsView.vue'

const mockSettingsData = {
  duplicateExclusions: [
    { field: 'email', value: 'test@example.com' },
    { field: 'phone', value: '+70000000000' }
  ],
  duplicateSearchOptions: {
    includeArchivedWithDocuments: true,
    groupLimit: 40,
    itemLimit: 100
  },
  fullSyncTimer: {
    enabled: true,
    runAt: '03:00'
  },
  searchLimits: {
    groupLimit: 40,
    itemLimit: 100
  },
  notifications: {
    enabled: true,
    email: 'ivanov.i@moisklad.ru',
    mode: 'errors'
  }
}
</script>

<template>
  <Story title="МойСклад/Настройки">
    <Variant title="Настройки">
      <div class="story-page">
        <MoyskladSettingsView :settings-data="mockSettingsData" />
      </div>
    </Variant>
  </Story>
</template>

<style src="../styles/stories/story-page.css"></style>
