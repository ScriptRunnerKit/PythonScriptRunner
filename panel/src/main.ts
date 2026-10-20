import { createPinia } from 'pinia'
import { createApp } from 'vue'
import App from './App.vue'
import { i18n } from './i18n'
import { vuetify } from './plugins/vuetify'
import { createAppRouter } from './router'
import { useSession } from './stores/session'

const app = createApp(App)
const pinia = createPinia()
const router = createAppRouter()
app.use(pinia).use(i18n).use(vuetify).use(router)
useSession(pinia).connect({ navigate: (target) => void router.push(target) })
app.mount('#app')
