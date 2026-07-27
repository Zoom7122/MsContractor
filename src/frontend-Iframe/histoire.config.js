import { defineConfig } from 'histoire'
import { HstVue } from '@histoire/plugin-vue'

export default defineConfig({
  plugins: [HstVue()],

  storyMatch: [
    'src/**/*.story.vue',
  ],


  theme: {
    title: 'mscontractor iframe UI',
    defaultColorScheme: 'light',
  },

  vite: {
    server: {
      host: '127.0.0.1',
      port: 6006,
    },
  },
})