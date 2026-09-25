<script setup>
import { computed } from 'vue'
import { Refresh, Switch } from '@element-plus/icons-vue'

import { DOCUMENT_TYPES } from '../../domain/merge'

/**
 * Explains what merge does with each kind of document when the concrete
 * list is not known in advance (documents are discovered during the job).
 */
const groups = computed(() => {
  const entries = Object.values(DOCUMENT_TYPES)
  return [
    {
      key: 'reassign',
      icon: Switch,
      title: 'Перепривязываются',
      description: 'Контрагент меняется прямо в документе — номер, дата и связи сохраняются.',
      types: entries.filter((item) => item.action === 'reassign').map((item) => item.label)
    },
    {
      key: 'recreate',
      icon: Refresh,
      title: 'Пересоздаются',
      description: 'МойСклад не даёт сменить контрагента в этих документах, поэтому создаётся копия на основного контрагента.',
      types: entries.filter((item) => item.action === 'recreate').map((item) => item.label)
    }
  ]
})
</script>

<template>
  <div class="document-rules">
    <div v-for="group in groups" :key="group.key" class="document-rules__card" :class="`document-rules__card--${group.key}`">
      <div class="document-rules__head">
        <span class="document-rules__icon" aria-hidden="true">
          <el-icon><component :is="group.icon" /></el-icon>
        </span>
        <span class="document-rules__title">{{ group.title }}</span>
      </div>
      <p class="document-rules__description">{{ group.description }}</p>
      <ul class="document-rules__types">
        <li v-for="type in group.types" :key="type">{{ type }}</li>
      </ul>
    </div>
  </div>
</template>

<style scoped src="../../styles/components/merge/merge-document-rules.css"></style>
