<script setup>
import { useRouter } from 'vue-router'

import EmptyState from '../components/ui/EmptyState.vue'
import PageHeader from '../components/ui/PageHeader.vue'
import SectionPanel from '../components/ui/SectionPanel.vue'

const router = useRouter()
</script>

<template>
  <div class="app-page">
    <PageHeader title="Синхронизация" subtitle="Загрузка контрагентов из МоегоСклада" />
    <SectionPanel>
      <EmptyState
        image="sync"
        title="Синхронизация запускается на странице «Обзор»"
        description="Там же виден прогресс текущего запуска. Расписание автоматической синхронизации — в настройках."
      >
        <el-button type="primary" @click="router.push({ name: 'moysklad-overview' })">Открыть обзор</el-button>
        <el-button @click="router.push({ name: 'moysklad-settings' })">Настройки расписания</el-button>
      </EmptyState>
    </SectionPanel>
  </div>
</template>
