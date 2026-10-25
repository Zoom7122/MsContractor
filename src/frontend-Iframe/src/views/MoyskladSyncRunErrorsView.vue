<script setup>
import { useRoute } from 'vue-router'
import SyncRunErrorsPanel from '../components/sync/SyncRunErrorsPanel.vue'
const route = useRoute()
</script>

<template>
  <SyncRunErrorsPanel :sync-run-id="String(route.params.syncRunId || '')" />
</template>
