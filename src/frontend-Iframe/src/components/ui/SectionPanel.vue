<script setup>
/**
 * White working surface with an optional header (title, subtitle, actions)
 * and footer. `flush` removes body padding for tables and lists.
 */
defineProps({
  title: { type: String, default: '' },
  subtitle: { type: String, default: '' },
  flush: { type: Boolean, default: false },
  as: { type: String, default: 'section' }
})
</script>

<template>
  <component :is="as" class="section-panel">
    <header v-if="title || $slots.title || $slots.actions" class="section-panel__header">
      <div class="section-panel__heading">
        <slot name="title">
          <h2 class="section-panel__title">{{ title }}</h2>
        </slot>
        <p v-if="subtitle || $slots.subtitle" class="section-panel__subtitle">
          <slot name="subtitle">{{ subtitle }}</slot>
        </p>
      </div>
      <div v-if="$slots.actions" class="section-panel__actions">
        <slot name="actions" />
      </div>
    </header>

    <div class="section-panel__body" :class="{ 'section-panel__body--flush': flush }">
      <slot />
    </div>

    <footer v-if="$slots.footer" class="section-panel__footer">
      <slot name="footer" />
    </footer>
  </component>
</template>

<style scoped src="../../styles/components/ui/section-panel.css"></style>
