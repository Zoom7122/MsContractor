<script setup>
import { computed } from 'vue'

/**
 * Status label that never relies on color alone: icon + text.
 * Pass `meta` ({ label, tone, icon, spinning }) from the domain helpers,
 * or the individual props.
 */
const props = defineProps({
  meta: { type: Object, default: null },
  label: { type: String, default: '' },
  tone: { type: String, default: 'neutral' },
  icon: { type: [Object, Function], default: null },
  spinning: { type: Boolean, default: false },
  size: { type: String, default: 'md' },
  variant: { type: String, default: 'soft' }
})

const resolved = computed(() => ({
  label: props.label || props.meta?.label || '',
  tone: props.meta?.tone || props.tone,
  icon: props.icon || props.meta?.icon || null,
  spinning: props.spinning || Boolean(props.meta?.spinning)
}))

const classes = computed(() => [
  'status-badge',
  `status-badge--${resolved.value.tone}`,
  `status-badge--${props.size}`,
  `status-badge--${props.variant}`
])
</script>

<template>
  <span :class="classes">
    <el-icon v-if="resolved.icon" class="status-badge__icon" :class="{ 'is-loading': resolved.spinning }" aria-hidden="true">
      <component :is="resolved.icon" />
    </el-icon>
    <span class="status-badge__label">
      <slot>{{ resolved.label }}</slot>
    </span>
  </span>
</template>

<style scoped src="../../styles/components/ui/status-badge.css"></style>
