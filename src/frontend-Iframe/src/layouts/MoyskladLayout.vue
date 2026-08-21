<script setup>
import { computed, onMounted, ref } from 'vue'
import { RouterView, useRoute } from 'vue-router'
import { Loading } from '@element-plus/icons-vue'

import AppSidebar from '../components/layout/AppSidebar.vue'
import { useMoyskladSession } from '../composables/useMoyskladSession'

const route = useRoute()
const sidebarCollapsed = ref(false)
const initialized = ref(false)

const pageTitle = computed(() => route.meta?.title || 'MS Contractor')
const pageSubtitle = computed(() => route.meta?.subtitle || 'Интерфейс решения внутри iframe МоегоСклада')
const pageSection = computed(() => {
  if (route.path.startsWith('/moysklad/history')) {
    return 'Журнал изменений'
  }
  if (route.path.startsWith('/moysklad/counterparties/')) {
    return 'Карточка контрагента'
  }
  return 'Рабочая область'
})
const { loading, error, isAuthenticated, initMoyskladSession } = useMoyskladSession()
const showLoading = computed(() => loading.value || !initialized.value)

onMounted(async () => {
  try {
    await initMoyskladSession()
  } catch {
    // Error state is stored in the composable and rendered below.
  } finally {
    initialized.value = true
  }
})
</script>

<template>
  <div v-if="showLoading" class="session-state">
    <el-card class="session-state__panel" shadow="never">
      <el-icon class="session-state__spinner is-loading" :size="28"><Loading /></el-icon>
      <h1>Открываем решение</h1>
      <p>Проверяем iframe-сессию МоегоСклада.</p>
    </el-card>
  </div>

  <div v-else-if="error || !isAuthenticated" class="session-state session-state--error">
    <el-card class="session-state__panel" shadow="never">
      <h1>Не удалось открыть решение</h1>
      <el-alert
        :title="error || 'Откройте решение из МоегоСклада'"
        type="error"
        :closable="false"
        show-icon
      />
    </el-card>
  </div>

  <div v-else class="moysklad-layout">
    <AppSidebar v-model:collapsed="sidebarCollapsed" />
    <main class="layout-main">
      <section class="layout-content">
        <RouterView />
      </section>
    </main>
  </div>
</template>

<style scoped src="../styles/layouts/moysklad-layout.css"></style>
