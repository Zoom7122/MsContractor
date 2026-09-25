<script setup>
import EmptyState from '../components/ui/EmptyState.vue'
import PageHeader from '../components/ui/PageHeader.vue'
import SectionPanel from '../components/ui/SectionPanel.vue'
</script>

<template>
  <div class="app-page">
    <PageHeader title="История документов" subtitle="Журнал изменений документов при объединениях" />
    <SectionPanel>
      <EmptyState
        image="documents"
        title="Раздел готовится"
        description="Здесь будет отдельный журнал изменений документов с фильтрами и деталями по связям."
      />
    </SectionPanel>
  </div>
</template>
