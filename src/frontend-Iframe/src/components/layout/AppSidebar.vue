<script setup>
import { computed, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import {
  ArrowLeft,
  ArrowRight,
  Clock,
  CopyDocument,
  HomeFilled,
  Setting
} from '@element-plus/icons-vue'

const emit = defineEmits({
  'update:collapsed': (value) => typeof value === 'boolean'
})

const route = useRoute()
const collapsed = ref(false)

const navItems = [
  { label: 'Обзор', to: '/moysklad/app', icon: HomeFilled },
  { label: 'Дубликаты', to: '/moysklad/duplicates', icon: CopyDocument },
  { label: 'История', to: '/moysklad/history', icon: Clock },
  { label: 'Настройки', to: '/moysklad/settings', icon: Setting }
]

const sidebarClasses = computed(() => ({
  'app-sidebar': true,
  'app-sidebar--collapsed': collapsed.value
}))

function toggleCollapsed() {
  collapsed.value = !collapsed.value
  emit('update:collapsed', collapsed.value)
}

function isActive(item) {
  return route.path === item.to || route.path.startsWith(`${item.to}/`)
}
</script>

<template>
  <aside :class="sidebarClasses" aria-label="Основная навигация">
    <div class="sidebar-top">
      <RouterLink class="brand" to="/moysklad/app" aria-label="MS Contractor">
        <span class="brand-mark">MS</span>
        <span class="brand-text">MS Contractor</span>
      </RouterLink>

      <el-button
        class="collapse-button"
        text
        :aria-label="collapsed ? 'Развернуть меню' : 'Свернуть меню'"
        :aria-expanded="!collapsed"
        @click="toggleCollapsed"
      >
        <el-icon><component :is="collapsed ? ArrowRight : ArrowLeft" /></el-icon>
      </el-button>
    </div>

    <nav class="sidebar-nav">
      <RouterLink
        v-for="item in navItems"
        :key="item.to"
        class="nav-link"
        :class="{ 'nav-link--active': isActive(item) }"
        :to="item.to"
        :title="collapsed ? item.label : undefined"
      >
        <el-icon class="nav-icon" aria-hidden="true"><component :is="item.icon" /></el-icon>
        <span class="nav-label">{{ item.label }}</span>
      </RouterLink>
    </nav>

    <div class="sidebar-footer">
      <span class="sidebar-footer__hint">Очереди, дубли, история и настройки в одном окне</span>
    </div>
  </aside>
</template>

<style scoped src="../../styles/components/app-sidebar.css"></style>
