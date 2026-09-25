<script setup>
import { computed, ref } from 'vue'
import { ArrowDown, ArrowRight, CircleCloseFilled, RefreshRight, WarningFilled } from '@element-plus/icons-vue'

import { toUserError } from '../../utils/errors'

/**
 * Readable error block. The headline is a user-facing message; technical data
 * (HTTP status, code, correlation id, raw backend text) is collapsed below.
 * `error` accepts a string, an Error from the http client, or `{ message, details }`.
 */
const props = defineProps({
  error: { type: [String, Object, Error], default: null },
  title: { type: String, default: '' },
  fallback: { type: String, default: 'Не удалось выполнить запрос' },
  description: { type: String, default: '' },
  tone: { type: String, default: 'danger' },
  retryable: { type: Boolean, default: false },
  retryLabel: { type: String, default: 'Повторить' },
  retrying: { type: Boolean, default: false }
})

const emit = defineEmits(['retry'])
const detailsOpen = ref(false)

const normalized = computed(() => {
  if (props.error && typeof props.error === 'object' && 'details' in props.error && 'message' in props.error) {
    return props.error
  }
  return toUserError(props.error, props.fallback) || { message: props.fallback, details: [] }
})

const headline = computed(() => props.title || normalized.value.message)
const body = computed(() => (props.title ? props.description || normalized.value.message : props.description))
const details = computed(() => normalized.value.details || [])
</script>

<template>
  <div class="error-notice" :class="`error-notice--${tone}`" role="alert">
    <el-icon class="error-notice__icon" aria-hidden="true">
      <WarningFilled v-if="tone === 'warning'" />
      <CircleCloseFilled v-else />
    </el-icon>

    <div class="error-notice__content">
      <p class="error-notice__title">{{ headline }}</p>
      <p v-if="body" class="error-notice__description">{{ body }}</p>
      <slot />

      <div v-if="details.length" class="error-notice__details">
        <button
          type="button"
          class="error-notice__toggle"
          :aria-expanded="detailsOpen"
          @click="detailsOpen = !detailsOpen"
        >
          <el-icon aria-hidden="true"><component :is="detailsOpen ? ArrowDown : ArrowRight" /></el-icon>
          Технические детали
        </button>
        <dl v-if="detailsOpen" class="error-notice__details-list">
          <div v-for="item in details" :key="item.label">
            <dt>{{ item.label }}</dt>
            <dd class="app-mono">{{ item.value }}</dd>
          </div>
        </dl>
      </div>
    </div>

    <div v-if="retryable || $slots.actions" class="error-notice__actions">
      <slot name="actions" />
      <el-button v-if="retryable" size="small" :loading="retrying" :icon="RefreshRight" @click="emit('retry')">
        {{ retryLabel }}
      </el-button>
    </div>
  </div>
</template>

<style scoped src="../../styles/components/ui/error-notice.css"></style>
