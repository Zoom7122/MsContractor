import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import ru from 'element-plus/es/locale/lang/ru'
import 'element-plus/dist/index.css'

import App from './App.vue'
import router from './router'
import { api } from './api/http'
import './styles/variables.css'
import './styles/base.css'
import './styles/element-plus-theme.css'

async function bootstrap() {
  // Dev-only UI mocks (npm run dev:mock). Dead code in production builds.
  if (import.meta.env.DEV && import.meta.env.VITE_UI_MOCKS === 'true') {
    const { installUiMocks } = await import('./mocks/install')
    installUiMocks({ api, router })
  }

  createApp(App).use(router).use(ElementPlus, { locale: ru }).mount('#app')
}

bootstrap()
