<script setup>
/**
 * Criteria a duplicate group was matched by. Always fully visible:
 * chips wrap onto new lines and the block grows in height — nothing is cut.
 * `criteria` — `[{ field, label, icon, value }]` (see domain/duplicates.groupCriteria).
 */
defineProps({
  criteria: { type: Array, default: () => [] },
  showValues: { type: Boolean, default: false },
  size: { type: String, default: 'md' }
})
</script>

<template>
  <ul class="match-criteria" :class="`match-criteria--${size}`" aria-label="Совпадения">
    <li
      v-for="criterion in criteria"
      :key="`${criterion.field}:${criterion.value}`"
      class="match-criteria__chip"
      :class="{ 'match-criteria__chip--valued': showValues && criterion.value }"
    >
      <el-icon class="match-criteria__icon" aria-hidden="true"><component :is="criterion.icon" /></el-icon>
      <span class="match-criteria__label">{{ criterion.label }}</span>
      <span v-if="showValues && criterion.value" class="match-criteria__value">{{ criterion.value }}</span>
    </li>
  </ul>
</template>

<style scoped src="../../styles/components/duplicates/match-criteria.css"></style>
