<script setup>
import { computed, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'

const emit = defineEmits({
  'update:collapsed': (value) => typeof value === 'boolean'
})

const route = useRoute()
const collapsed = ref(false)

const navItems = [
  { label: 'Обзор', to: '/moysklad/app', icon: '⌂' },
  { label: 'Дубликаты', to: '/moysklad/duplicates', icon: '≋' },
  { label: 'История', to: '/moysklad/history', icon: '◷' },
  { label: 'Настройки', to: '/moysklad/settings', icon: '⚙' }
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

      <button
        class="collapse-button"
        type="button"
        :aria-label="collapsed ? 'Развернуть меню' : 'Свернуть меню'"
        :aria-expanded="!collapsed"
        @click="toggleCollapsed"
      >
        <span class="collapse-icon">{{ collapsed ? '›' : '‹' }}</span>
      </button>
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
        <span class="nav-icon" aria-hidden="true">{{ item.icon }}</span>
        <span class="nav-label">{{ item.label }}</span>
      </RouterLink>
    </nav>

    <div class="sidebar-footer">
      <span class="sidebar-footer__hint">Очереди, дубли, история и настройки в одном окне</span>
    </div>
  </aside>
</template>

<style scoped>
.app-sidebar {
  --sidebar-open-width: 264px;
  --sidebar-collapsed-width: 76px;
  --sidebar-bg: #07162f;
  --sidebar-border: rgba(148, 163, 184, 0.18);
  --sidebar-text: #dbe7ff;
  --sidebar-muted: #8ea4c8;
  --sidebar-active: #1d4ed8;
  --sidebar-active-text: #ffffff;

  position: relative;
  display: flex;
  flex: 0 0 var(--sidebar-open-width);
  width: var(--sidebar-open-width);
  height: 100vh;
  min-height: 100vh;
  padding: 18px 14px;
  flex-direction: column;
  overflow: hidden;
  color: var(--sidebar-text);
  background:
    radial-gradient(circle at top, rgba(59, 130, 246, 0.22), transparent 22%),
    linear-gradient(180deg, #08162d 0%, #07162f 58%, #0b1a33 100%);
  border-right: 1px solid var(--sidebar-border);
  box-shadow: inset -1px 0 0 rgba(255, 255, 255, 0.04);
  transition:
    width 180ms ease,
    flex-basis 180ms ease,
    padding 180ms ease;
}

.app-sidebar--collapsed {
  flex-basis: var(--sidebar-collapsed-width);
  width: var(--sidebar-collapsed-width);
  padding-inline: 12px;
}

.sidebar-top {
  position: relative;
  display: flex;
  min-height: 44px;
  align-items: center;
  gap: 10px;
}

.brand {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 10px;
  color: inherit;
  text-decoration: none;
}

.brand-mark {
  display: inline-grid;
  width: 40px;
  height: 40px;
  flex: 0 0 40px;
  place-items: center;
  border-radius: 12px;
  color: #ffffff;
  background: linear-gradient(135deg, #2563eb, #1d4ed8);
  font-size: 13px;
  font-weight: 800;
  letter-spacing: 0;
  box-shadow: 0 12px 28px rgba(37, 99, 235, 0.32);
}

.brand-text,
.nav-label {
  overflow: hidden;
  white-space: nowrap;
  opacity: 1;
  transform: translateX(0);
  transition:
    opacity 140ms ease,
    transform 160ms ease,
    max-width 180ms ease;
}

.brand-text {
  max-width: 156px;
  font-size: 16px;
  font-weight: 750;
  letter-spacing: 0;
}

.collapse-button {
  display: inline-grid;
  width: 32px;
  height: 32px;
  margin-left: auto;
  flex: 0 0 32px;
  place-items: center;
  border: 1px solid var(--sidebar-border);
  border-radius: 8px;
  color: var(--sidebar-text);
  background: rgba(255, 255, 255, 0.06);
  cursor: pointer;
  transition:
    background 140ms ease,
    border-color 140ms ease;
}

.collapse-button:hover {
  background: rgba(255, 255, 255, 0.1);
  border-color: rgba(219, 231, 255, 0.28);
}

.collapse-button:focus-visible,
.nav-link:focus-visible,
.brand:focus-visible {
  outline: 2px solid #60a5fa;
  outline-offset: 2px;
}

.collapse-icon {
  font-size: 23px;
  line-height: 1;
}

.sidebar-nav {
  display: flex;
  min-height: 0;
  margin-top: 28px;
  flex: 1 1 auto;
  flex-direction: column;
  gap: 6px;
}

.nav-link {
  display: flex;
  min-width: 0;
  height: 42px;
  align-items: center;
  gap: 12px;
  padding: 0 12px;
  border-radius: 12px;
  color: var(--sidebar-muted);
  text-decoration: none;
  transition:
    color 140ms ease,
    background 140ms ease,
    transform 140ms ease;
}

.nav-link:hover {
  color: var(--sidebar-text);
  background: rgba(255, 255, 255, 0.07);
  transform: translateX(2px);
}

.nav-link.router-link-active,
.nav-link--active {
  color: var(--sidebar-active-text);
  background: linear-gradient(135deg, #1d4ed8, #235fcb);
  box-shadow: 0 10px 24px rgba(29, 78, 216, 0.24);
}

.nav-icon {
  display: inline-grid;
  width: 24px;
  height: 24px;
  flex: 0 0 24px;
  place-items: center;
  font-size: 18px;
  line-height: 1;
}

.nav-label {
  max-width: 172px;
  font-size: 14px;
  font-weight: 650;
}

.sidebar-footer {
  display: grid;
  gap: 6px;
  margin-top: auto;
  padding: 14px;
  background: rgba(255, 255, 255, 0.05);
  border: 1px solid rgba(148, 163, 184, 0.14);
  border-radius: 14px;
}

.sidebar-footer__label {
  color: #8ea4c8;
  font-size: 11px;
  font-weight: 800;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.sidebar-footer__value {
  color: #ffffff;
  font-size: 14px;
  font-weight: 800;
}

.sidebar-footer__hint {
  color: #b4c5e3;
  font-size: 12px;
  line-height: 1.4;
}

.app-sidebar--collapsed .brand-text,
.app-sidebar--collapsed .nav-label {
  max-width: 0;
  opacity: 0;
  pointer-events: none;
  transform: translateX(-8px);
}

.app-sidebar--collapsed .sidebar-top {
  justify-content: center;
}

.app-sidebar--collapsed .brand {
  width: 40px;
  flex: 0 0 40px;
}

.app-sidebar--collapsed .collapse-button {
  position: absolute;
  top: 48px;
  right: 9px;
  width: 28px;
  height: 28px;
}

.app-sidebar--collapsed .sidebar-nav {
  margin-top: 44px;
}

.app-sidebar--collapsed .sidebar-footer {
  padding: 10px 8px;
}

.app-sidebar--collapsed .sidebar-footer__label,
.app-sidebar--collapsed .sidebar-footer__hint,
.app-sidebar--collapsed .sidebar-footer__value {
  max-height: 0;
  overflow: hidden;
  opacity: 0;
}

.app-sidebar--collapsed .nav-link {
  justify-content: center;
  padding-inline: 0;
}
</style>
