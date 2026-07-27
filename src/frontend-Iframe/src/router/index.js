import { createRouter, createWebHistory } from 'vue-router'

import MoyskladLayout from '../layouts/MoyskladLayout.vue'
import MoyskladCounterpartyView from '../views/MoyskladCounterpartyView.vue'
import MoyskladDuplicatesView from '../views/MoyskladDuplicatesView.vue'
import MoyskladHistoryView from '../views/MoyskladHistoryView.vue'
import MoyskladMergeView from '../views/MoyskladMergeView.vue'
import MoyskladOverviewView from '../views/MoyskladOverviewView.vue'
import MoyskladSettingsView from '../views/MoyskladSettingsView.vue'
import MoyskladSyncView from '../views/MoyskladSyncView.vue'

const moyskladChildren = [
  {
    path: 'app',
    name: 'moysklad-overview',
    title: 'Обзор',
    subtitle: 'Сводка по синхронизации, очередям и данным МоегоСклада',
    component: MoyskladOverviewView
  },
  {
    path: 'duplicates',
    name: 'moysklad-duplicates',
    title: 'Дубликаты',
    subtitle: 'Поиск и объединение дублей контрагентов',
    component: MoyskladDuplicatesView
  },
  {
    path: 'merge',
    name: 'moysklad-merge',
    title: 'Объединения',
    subtitle: 'Предпросмотр итоговых полей и постановка merge в очередь',
    component: MoyskladMergeView
  },
  {
    path: 'sync',
    name: 'moysklad-sync',
    title: 'Синхронизация',
    subtitle: 'Управление полной и инкрементальной синхронизацией',
    component: MoyskladSyncView
  },
  {
    path: 'history',
    name: 'moysklad-history-counterparties',
    title: 'История изменений КА',
    subtitle: 'Журнал ручных правок, merge-операций и изменений через API',
    component: MoyskladHistoryView
  },
  { path: 'history/counterparties', redirect: '/moysklad/history' },
  {
    path: 'counterparties/:id',
    name: 'moysklad-counterparty',
    title: 'Карточка контрагента',
    subtitle: 'Детальная информация по КА, экспортам и связанным документам',
    component: MoyskladCounterpartyView
  },
  { path: 'history/documents', redirect: '/moysklad/history' },
  {
    path: 'settings',
    name: 'moysklad-settings',
    title: 'Настройки',
    subtitle: 'Параметры синхронизации, merge и доп. полей',
    component: MoyskladSettingsView
  }
]

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      redirect: '/moysklad/app'
    },
    {
      path: '/moysklad',
      component: MoyskladLayout,
      redirect: '/moysklad/app',
      children: moyskladChildren.map((route) => {
        if (route.redirect) {
          return {
            path: route.path,
            name: route.name,
            redirect: route.redirect
          }
        }

        return {
          path: route.path,
          name: route.name,
          component: route.component,
          meta: { title: route.title, subtitle: route.subtitle }
        }
      })
    }
  ]
})

export default router
