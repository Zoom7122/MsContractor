<script setup>
import CounterpartyChangeHistoryPanel from '../components/history/CounterpartyChangeHistoryPanel.vue'

defineProps({
  historyData: { type: Object, default: null },
  loading: { type: Boolean, default: false },
  loadError: { type: [String, Object], default: null }
})
</script>

<template>
  <CounterpartyChangeHistoryPanel :data="historyData" :loading="loading" :load-error="loadError" />
</template>
