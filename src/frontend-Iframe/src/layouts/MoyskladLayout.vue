<script setup>
import { computed, onMounted, ref } from 'vue'
import { RouterView, useRoute } from 'vue-router'

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
    <div class="session-state__panel">
      <div class="session-state__spinner" />
      <h1>Открываем решение</h1>
      <p>Проверяем iframe-сессию МоегоСклада.</p>
    </div>
  </div>

  <div v-else-if="error || !isAuthenticated" class="session-state session-state--error">
    <div class="session-state__panel">
      <h1>Не удалось открыть решение</h1>
      <p>{{ error || 'Откройте решение из МоегоСклада' }}</p>
    </div>
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

<style scoped>
.moysklad-layout {
  display: flex;
  width: 100%;
  height: 100vh;
  min-width: 0;
  overflow: hidden;
  background:
    radial-gradient(circle at top left, rgba(45, 108, 223, 0.1), transparent 24%),
    linear-gradient(180deg, #f6f9fe 0%, #eef3fa 100%);
}

.layout-main {
  display: flex;
  min-width: 0;
  min-height: 0;
  flex: 1 1 auto;
  flex-direction: column;
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.46), rgba(255, 255, 255, 0)),
    transparent;
}

.layout-header {
  display: flex;
  min-height: 74px;
  flex: 0 0 74px;
  align-items: flex-start;
  justify-content: space-between;
  gap: 20px;
  padding: 12px 24px 10px;
  background: rgba(255, 255, 255, 0.78);
  border-bottom: 1px solid rgba(218, 228, 242, 0.92);
  backdrop-filter: blur(12px);
}

.header-title-group {
  min-width: 0;
}

.header-kicker {
  display: inline-flex;
  margin-bottom: 4px;
  color: #3c5a8b;
  font-size: 12px;
  font-weight: 800;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.layout-header h1 {
  margin: 0 0 2px;
  overflow: hidden;
  color: #0f1b3d;
  font-size: 22px;
  font-weight: 800;
  letter-spacing: 0;
  line-height: 1.2;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.layout-header p {
  margin: 0;
  color: #64759b;
  font-size: 14px;
  line-height: 1.4;
}

.mode-badge {
  display: inline-flex;
  min-height: 32px;
  flex: 0 0 auto;
  align-items: center;
  padding: 0 12px;
  border: 1px solid #d2dff2;
  border-radius: 999px;
  color: #335c99;
  background: linear-gradient(180deg, #f8fbff, #edf4ff);
  font-size: 12px;
  font-weight: 800;
}

.layout-content {
  min-height: 0;
  flex: 1 1 auto;
  overflow: auto;
  padding: 12px 20px 24px;
}

.session-state {
  display: flex;
  min-height: 100vh;
  align-items: center;
  justify-content: center;
  padding: 24px;
  background: #f5f7fb;
}

.session-state__panel {
  width: min(420px, 100%);
  padding: 30px;
  border: 1px solid #dbe4f0;
  border-radius: 18px;
  background: #ffffff;
  box-shadow: 0 18px 40px rgb(15 23 42 / 10%);
  text-align: center;
}

.session-state__panel h1 {
  margin: 0 0 10px;
  color: #111827;
  font-size: 20px;
  font-weight: 750;
  letter-spacing: 0;
  line-height: 1.2;
}

.session-state__panel p {
  margin: 0;
  color: #52637a;
  font-size: 14px;
  line-height: 1.5;
}

.session-state__spinner {
  width: 28px;
  height: 28px;
  margin: 0 auto 16px;
  border: 3px solid #dbe4f0;
  border-top-color: #2563eb;
  border-radius: 999px;
  animation: session-spin 0.8s linear infinite;
}

.session-state--error .session-state__panel {
  border-color: #fecaca;
}

.session-state--error .session-state__panel h1 {
  color: #991b1b;
}

@keyframes session-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (max-width: 760px) {
  .layout-header {
    min-height: auto;
    flex-basis: auto;
    flex-direction: column;
    padding: 12px 18px 10px;
  }

  .layout-header h1 {
    white-space: normal;
  }

  .layout-content {
    padding: 14px;
  }
}
</style>
