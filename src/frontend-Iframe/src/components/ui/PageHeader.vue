<script setup>
import { RouterLink } from 'vue-router'
import { ArrowLeft } from '@element-plus/icons-vue'

/**
 * Compact page title row: optional back link, title, subtitle,
 * inline meta (badges/counters) and right-aligned actions.
 */
defineProps({
  title: { type: String, required: true },
  subtitle: { type: String, default: '' },
  backTo: { type: [String, Object], default: null },
  showBack: { type: Boolean, default: false },
  backLabel: { type: String, default: 'Назад' }
})

const emit = defineEmits(['back'])
</script>

<template>
  <header class="page-header">
    <div class="page-header__main">
      <RouterLink v-if="backTo" class="page-header__back" :to="backTo">
        <el-icon aria-hidden="true"><ArrowLeft /></el-icon>
        <span>{{ backLabel }}</span>
      </RouterLink>
      <button v-else-if="showBack" type="button" class="page-header__back" @click="emit('back')">
        <el-icon aria-hidden="true"><ArrowLeft /></el-icon>
        <span>{{ backLabel }}</span>
      </button>

      <div class="page-header__title-row">
        <h1 class="page-header__title">{{ title }}</h1>
        <div v-if="$slots.meta" class="page-header__meta">
          <slot name="meta" />
        </div>
      </div>
      <p v-if="subtitle || $slots.subtitle" class="page-header__subtitle">
        <slot name="subtitle">{{ subtitle }}</slot>
      </p>
    </div>

    <div v-if="$slots.actions" class="page-header__actions">
      <slot name="actions" />
    </div>
  </header>
</template>

<style scoped src="../../styles/components/ui/page-header.css"></style>
