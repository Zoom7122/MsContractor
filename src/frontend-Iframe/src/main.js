import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import ru from 'element-plus/es/locale/lang/ru'
import 'element-plus/dist/index.css'

import App from './App.vue'
import router from './router'
import './styles/variables.css'
import './styles/base.css'
import './styles/element-plus-theme.css'

createApp(App).use(router).use(ElementPlus, { locale: ru }).mount('#app')
