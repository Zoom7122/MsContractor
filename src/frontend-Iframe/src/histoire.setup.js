import { defineSetupVue3 } from '@histoire/plugin-vue'
import ElementPlus from 'element-plus'
import ru from 'element-plus/es/locale/lang/ru'
import { createMemoryHistory, createRouter } from 'vue-router'
import 'element-plus/dist/index.css'

import './styles/variables.css'
import './styles/base.css'
import './styles/element-plus-theme.css'

export const setupVue3 = defineSetupVue3(({ app }) => {
  const storyRouter = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'histoire-home', component: { render: () => null } },
      { path: '/app', name: 'moysklad-overview', component: { render: () => null } },
      { path: '/settings', name: 'moysklad-settings', component: { render: () => null } },
      { path: '/duplicates', name: 'moysklad-duplicates', component: { render: () => null } },
      { path: '/merge', name: 'moysklad-merge', component: { render: () => null } },
      { path: '/history', name: 'moysklad-history-counterparties', component: { render: () => null } },
      { path: '/counterparties/:id', name: 'moysklad-counterparty', component: { render: () => null } }
    ]
  })

  app.use(storyRouter)
  app.use(ElementPlus, { locale: ru })
})
