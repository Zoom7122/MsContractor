<script setup>
import EmptyIllustration from './EmptyIllustration.vue'

/**
 * Explains why a block is empty and what to do next.
 * `image` — illustration name (see EmptyIllustration), `size` — 'sm' | 'md'.
 */
defineProps({
  title: { type: String, required: true },
  description: { type: String, default: '' },
  image: { type: String, default: 'search' },
  size: { type: String, default: 'md' },
  bordered: { type: Boolean, default: false }
})
</script>

<template>
  <div class="empty-state" :class="[`empty-state--${size}`, { 'empty-state--bordered': bordered }]" role="status">
    <div class="empty-state__image">
      <EmptyIllustration :name="image" />
    </div>
    <div class="empty-state__text">
      <p class="empty-state__title">{{ title }}</p>
      <p v-if="description || $slots.description" class="empty-state__description">
        <slot name="description">{{ description }}</slot>
      </p>
    </div>
    <div v-if="$slots.default" class="empty-state__actions">
      <slot />
    </div>
  </div>
</template>

<style scoped src="../../styles/components/ui/empty-state.css"></style>
