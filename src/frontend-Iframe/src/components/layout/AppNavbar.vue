<script setup>
import { RouterLink, useRoute } from 'vue-router'
import { CopyDocument, House, Setting } from '@element-plus/icons-vue'

import BrandMark from './BrandMark.vue'

/**
 * Top navigation in the spirit of MoySklad second-level tabs.
 * Horizontal layout keeps the full iframe width for tables.
 */
const route = useRoute()

const navItems = [
  { label: 'Обзор', to: '/moysklad/app', icon: House, match: ['/moysklad/app'] },
  { label: 'Дубликаты', to: '/moysklad/duplicates', icon: CopyDocument, match: ['/moysklad/duplicates', '/moysklad/merge'] },
  { label: 'Настройки', to: '/moysklad/settings', icon: Setting, match: ['/moysklad/settings'] }
]

function isActive(item) {
  return item.match.some((prefix) => route.path === prefix || route.path.startsWith(`${prefix}/`))
}
</script>

<template>
  <header class="app-navbar">
    <RouterLink class="app-navbar__brand" to="/moysklad/app" aria-label="MS Contractor — обзор">
      <BrandMark class="app-navbar__logo" />
      <span class="app-navbar__brand-text">MS Contractor</span>
    </RouterLink>

    <nav class="app-navbar__nav" aria-label="Разделы">
      <RouterLink
        v-for="item in navItems"
        :key="item.to"
        class="app-navbar__link"
        :class="{ 'app-navbar__link--active': isActive(item) }"
        :to="item.to"
        :aria-current="isActive(item) ? 'page' : undefined"
      >
        <el-icon class="app-navbar__icon" aria-hidden="true"><component :is="item.icon" /></el-icon>
        <span>{{ item.label }}</span>
      </RouterLink>
    </nav>

    <div v-if="$slots.default" class="app-navbar__aside">
      <slot />
    </div>
  </header>
</template>

<style scoped src="../../styles/components/app-navbar.css"></style>
