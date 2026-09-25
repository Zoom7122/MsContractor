<script setup>
import { Close, Promotion } from '@element-plus/icons-vue'

import CopyableId from './CopyableId.vue'

/**
 * Result of an asynchronous command answered with `202 Accepted`:
 * the request is queued, not finished. Shows what happens next and the
 * operation id as secondary metadata.
 */
defineProps({
  title: { type: String, required: true },
  description: { type: String, default: '' },
  operationId: { type: String, default: '' },
  operationLabel: { type: String, default: 'ID задачи' },
  closable: { type: Boolean, default: false }
})

const emit = defineEmits(['close'])
</script>

<template>
  <div class="accepted-notice" role="status">
    <span class="accepted-notice__icon" aria-hidden="true">
      <el-icon><Promotion /></el-icon>
    </span>

    <div class="accepted-notice__content">
      <p class="accepted-notice__title">{{ title }}</p>
      <p v-if="description || $slots.description" class="accepted-notice__description">
        <slot name="description">{{ description }}</slot>
      </p>
      <CopyableId v-if="operationId" :value="operationId" :label="operationLabel" />
    </div>

    <div v-if="$slots.actions" class="accepted-notice__actions">
      <slot name="actions" />
    </div>

    <el-button
      v-if="closable"
      class="accepted-notice__close"
      text
      circle
      size="small"
      :icon="Close"
      aria-label="Скрыть уведомление"
      @click="emit('close')"
    />
  </div>
</template>

<style scoped src="../../styles/components/ui/accepted-notice.css"></style>
