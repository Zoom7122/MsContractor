<script setup>
import { computed, onMounted, ref } from 'vue'
import { RouterView } from 'vue-router'

import AppNavbar from '../components/layout/AppNavbar.vue'
import BrandMark from '../components/layout/BrandMark.vue'
import ErrorNotice from '../components/ui/ErrorNotice.vue'
import { useMoyskladSession } from '../composables/useMoyskladSession'

const initialized = ref(false)

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
  <div v-if="showLoading" class="session-state" aria-busy="true">
    <div class="session-state__panel">
      <BrandMark class="session-state__brand" />
      <h1 class="session-state__title">Открываем MS Contractor</h1>
      <p class="session-state__text">Проверяем сессию МоегоСклада…</p>
      <div class="session-state__progress" aria-hidden="true" />
    </div>
  </div>

  <div v-else-if="error || !isAuthenticated" class="session-state">
    <div class="session-state__panel">
      <BrandMark class="session-state__brand" />
      <h1 class="session-state__title">Не удалось открыть решение</h1>
      <p class="session-state__text">
        Решение работает только внутри МоегоСклада — сессия создаётся при открытии из интерфейса.
      </p>
      <ol class="session-state__steps">
        <li>Откройте МойСклад в соседней вкладке.</li>
        <li>Перейдите в раздел решения MS Contractor.</li>
        <li>Если ошибка повторяется — обновите страницу МоегоСклада.</li>
      </ol>
      <ErrorNotice
        class="session-state__error"
        tone="warning"
        :error="error || 'Откройте решение из МоегоСклада'"
      />
    </div>
  </div>

  <div v-else class="moysklad-layout">
    <AppNavbar />
    <main class="layout-main">
      <div class="layout-content">
        <RouterView />
      </div>
    </main>
  </div>
</template>

<style scoped src="../styles/layouts/moysklad-layout.css"></style>
