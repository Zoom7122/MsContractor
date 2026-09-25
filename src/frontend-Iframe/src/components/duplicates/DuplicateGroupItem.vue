<script setup>
import { computed } from 'vue'

import MatchCriteria from './MatchCriteria.vue'
import { groupCriteria } from '../../domain/duplicates'

/** Compact row of the duplicate groups list. */
const props = defineProps({
  group: { type: Object, required: true },
  active: { type: Boolean, default: false },
  title: { type: String, required: true }
})

defineEmits(['select'])

const criteria = computed(() => groupCriteria(props.group))
const names = computed(() => {
  const unique = [...new Set(props.group.counterparties.map((item) => item.name).filter(Boolean))]
  return unique.filter((name) => name !== props.title).join(', ')
})
const archivedCount = computed(() => props.group.counterparties.filter((item) => item.archived).length)
</script>

<template>
  <button
    type="button"
    class="duplicate-group"
    :class="{ 'duplicate-group--active': active }"
    :aria-current="active ? 'true' : undefined"
    @click="$emit('select', group.key)"
  >
    <span class="duplicate-group__head">
      <span class="duplicate-group__title">{{ title }}</span>
      <span class="duplicate-group__count app-nums" :title="`${group.counterparties.length} контрагентов в группе`">
        {{ group.counterparties.length }}
      </span>
    </span>
    <span v-if="names" class="duplicate-group__names app-truncate">{{ names }}</span>
    <span class="duplicate-group__footer">
      <MatchCriteria :criteria="criteria" size="sm" />
      <span v-if="archivedCount" class="duplicate-group__meta">архивных: {{ archivedCount }}</span>
      <span v-if="group.itemsTruncated" class="duplicate-group__meta duplicate-group__meta--warning">показаны не все</span>
    </span>
  </button>
</template>

<style scoped src="../../styles/components/duplicates/duplicate-group-item.css"></style>
