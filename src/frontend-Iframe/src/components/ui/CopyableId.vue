<script setup>
import { computed, ref } from 'vue'
import { Check, CopyDocument } from '@element-plus/icons-vue'

import { shortId } from '../../utils/format'

/** Muted technical id: shows a short form, copies the full value. */
const props = defineProps({
  value: { type: String, default: '' },
  label: { type: String, default: '' },
  full: { type: Boolean, default: false }
})

const copied = ref(false)
const display = computed(() => (props.full ? props.value : shortId(props.value)))

async function copy() {
  if (!props.value) {
    return
  }

  try {
    await navigator.clipboard.writeText(props.value)
    copied.value = true
    window.setTimeout(() => {
      copied.value = false
    }, 1500)
  } catch {
    copied.value = false
  }
}
</script>

<template>
  <span v-if="value" class="copyable-id">
    <span v-if="label" class="copyable-id__label">{{ label }}</span>
    <el-tooltip :content="copied ? 'Скопировано' : `Скопировать: ${value}`" placement="top" :show-after="300">
      <button type="button" class="copyable-id__button app-mono" @click.stop="copy">
        {{ display }}
        <el-icon aria-hidden="true"><component :is="copied ? Check : CopyDocument" /></el-icon>
      </button>
    </el-tooltip>
  </span>
</template>

<style scoped src="../../styles/components/ui/copyable-id.css"></style>
