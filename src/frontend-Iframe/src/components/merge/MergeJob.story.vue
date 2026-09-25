<script setup>
import MergeDocumentsTable from './MergeDocumentsTable.vue'
import MergeJobProgress from './MergeJobProgress.vue'
import { normalizeMergeJob } from '../../domain/merge'
import { buildMergeJobs } from '../../mocks/fixtures/merge'

const jobs = buildMergeJobs('normal').map(normalizeMergeJob)
const partialJob = jobs.find((job) => job.status === 'partial')
</script>

<template>
  <Story title="Merge/Ход выполнения">
    <Variant title="Компактный прогресс">
      <div class="story-page story-stack">
        <div v-for="job in jobs" :key="job.id" class="story-card">
          <strong>{{ job.status }}</strong>
          <MergeJobProgress :job="job" />
        </div>
      </div>
    </Variant>
    <Variant title="Этапы: частичная ошибка">
      <div class="story-page">
        <div class="story-card"><MergeJobProgress :job="partialJob" variant="steps" /></div>
      </div>
    </Variant>
    <Variant title="Документы: результат">
      <div class="story-page">
        <div class="story-card"><MergeDocumentsTable :documents="partialJob.documents" mode="result" /></div>
      </div>
    </Variant>
  </Story>
</template>

<style src="../../styles/stories/story-page.css"></style>
