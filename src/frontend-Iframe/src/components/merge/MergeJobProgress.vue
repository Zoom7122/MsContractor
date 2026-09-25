<script setup>
import { computed } from 'vue'
import { CircleCheckFilled, CircleCloseFilled, Loading, Minus, WarningFilled } from '@element-plus/icons-vue'

import ErrorNotice from '../ui/ErrorNotice.vue'
import StatusBadge from '../ui/StatusBadge.vue'
import { STEP_STATUS, buildJobStages, normalizeJobStatus, operationLabel } from '../../domain/merge'

/**
 * Merge job progress by stage.
 * `compact` — segmented bar with a caption (queue rows);
 * `steps` — vertical stepper with operations and errors (details drawer).
 */
const props = defineProps({
  job: { type: Object, required: true },
  variant: { type: String, default: 'compact' }
})

const stages = computed(() => buildJobStages(props.job))
const jobStatus = computed(() => normalizeJobStatus(props.job?.status))

const caption = computed(() => {
  const list = stages.value
  const failed = list.filter((stage) => stage.key !== 'finish' && (stage.status === 'failed' || stage.status === 'partial'))
  const running = list.find((stage) => stage.status === 'running')
  const doneCount = list.filter((stage) => stage.status === 'completed' || stage.status === 'skipped').length

  if (jobStatus.value === 'pending') {
    return 'Ожидает запуска'
  }
  if (jobStatus.value === 'completed') {
    return 'Все этапы выполнены'
  }
  if (jobStatus.value === 'partial') {
    return `Ошибки на этапах: ${failed.map((stage) => stage.label).join(', ')}`
  }
  if (jobStatus.value === 'failed') {
    return failed.length ? `Остановлено на этапе «${failed[0].label}»` : 'Остановлено с ошибкой'
  }
  if (running) {
    return `${running.label} · этап ${list.indexOf(running) + 1} из ${list.length}`
  }
  return `Выполнено этапов: ${doneCount} из ${list.length}`
})

const markerIcons = {
  completed: CircleCheckFilled,
  failed: CircleCloseFilled,
  partial: WarningFilled,
  running: Loading,
  skipped: Minus
}

function stageCount(stage) {
  if (stage.total <= 1) {
    return ''
  }
  return `${stage.done} из ${stage.total}`
}

function operationTitle(operation) {
  const label = operationLabel(operation.type)
  return operation.counterpartyName ? `${label}: ${operation.counterpartyName}` : label
}

function showOperations(stage) {
  return stage.items.length > 1 || stage.items.some((item) => item.status === 'failed' || item.errorMessage)
}

function operationError(operation) {
  const readable = /[а-яё]/i.test(operation.errorMessage)
  const details = [
    operation.errorCode ? { label: 'Код', value: operation.errorCode } : null,
    operation.attemptCount ? { label: 'Попыток', value: String(operation.attemptCount) } : null,
    !readable && operation.errorMessage ? { label: 'Сообщение', value: operation.errorMessage } : null
  ].filter(Boolean)

  let message = 'Операция завершилась с ошибкой'
  if (readable) {
    message = operation.errorMessage
  } else if (operation.attemptCount > 1) {
    message = `Не удалось выполнить операцию за ${operation.attemptCount} попыток — МойСклад не принял изменения`
  }

  return { message, details }
}
</script>

<template>
  <div v-if="variant === 'compact'" class="job-progress">
    <div class="job-progress__bar" role="img" :aria-label="caption">
      <span
        v-for="stage in stages"
        :key="stage.key"
        class="job-progress__segment"
        :class="`job-progress__segment--${stage.status}`"
        :title="`${stage.label}: ${STEP_STATUS[stage.status].label}`"
      />
    </div>
    <span class="job-progress__caption" :class="`job-progress__caption--${jobStatus}`">{{ caption }}</span>
  </div>

  <ol v-else class="job-steps">
    <li
      v-for="(stage, index) in stages"
      :key="stage.key"
      class="job-step"
      :class="`job-step--${stage.status}`"
    >
      <span class="job-step__marker" aria-hidden="true">
        <el-icon v-if="markerIcons[stage.status]" :class="{ 'is-loading': stage.status === 'running' }">
          <component :is="markerIcons[stage.status]" />
        </el-icon>
        <span v-else>{{ index + 1 }}</span>
      </span>

      <div class="job-step__body">
        <div class="job-step__head">
          <span class="job-step__title">{{ stage.label }}</span>
          <StatusBadge size="sm" :meta="STEP_STATUS[stage.status]" />
          <span v-if="stageCount(stage)" class="job-step__count app-nums">{{ stageCount(stage) }}</span>
        </div>
        <p class="job-step__hint">{{ stage.hint }}</p>

        <ul v-if="showOperations(stage)" class="job-step__operations">
          <li v-for="operation in stage.items" :key="operation.id" class="job-step__operation">
            <div class="job-step__operation-head">
              <span class="job-step__operation-title">{{ operationTitle(operation) }}</span>
              <StatusBadge size="sm" variant="plain" :meta="STEP_STATUS[operation.status] || STEP_STATUS.pending" />
            </div>
            <ErrorNotice
              v-if="operation.status === 'failed'"
              class="job-step__error"
              :error="operationError(operation)"
            />
          </li>
        </ul>
      </div>
    </li>
  </ol>
</template>

<style scoped src="../../styles/components/merge/merge-job-progress.css"></style>
