<script setup>
import { RouterLink } from 'vue-router'
import { ArrowRight } from '@element-plus/icons-vue'

/** Metric with a label, a large value and a short hint. Optional link to details. */
defineProps({
  label: { type: String, required: true },
  value: { type: [String, Number], default: '—' },
  hint: { type: String, default: '' },
  icon: { type: [Object, Function], default: null },
  tone: { type: String, default: 'primary' },
  to: { type: [String, Object], default: null },
  linkLabel: { type: String, default: 'Открыть' },
  loading: { type: Boolean, default: false }
})
</script>

<template>
  <div class="stat-tile">
    <div class="stat-tile__head">
      <span v-if="icon" class="stat-tile__icon" :class="`stat-tile__icon--${tone}`" aria-hidden="true">
        <el-icon><component :is="icon" /></el-icon>
      </span>
      <span class="stat-tile__label">{{ label }}</span>
    </div>

    <el-skeleton v-if="loading" class="stat-tile__skeleton" animated>
      <template #template>
        <el-skeleton-item variant="h3" style="width: 60%; height: 26px" />
        <el-skeleton-item variant="text" style="width: 80%; margin-top: 6px" />
      </template>
    </el-skeleton>

    <template v-else>
      <div class="stat-tile__value app-nums">
        <slot name="value">{{ value }}</slot>
      </div>
      <div class="stat-tile__footer">
        <span v-if="hint || $slots.hint" class="stat-tile__hint">
          <slot name="hint">{{ hint }}</slot>
        </span>
        <RouterLink v-if="to" class="stat-tile__link" :to="to">
          {{ linkLabel }}
          <el-icon aria-hidden="true"><ArrowRight /></el-icon>
        </RouterLink>
      </div>
    </template>
  </div>
</template>

<style scoped src="../../styles/components/ui/stat-tile.css"></style>
