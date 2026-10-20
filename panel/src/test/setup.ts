import { config } from '@vue/test-utils'
import { createVuetify } from 'vuetify'
import { i18n } from '@/i18n'

// Vuetify mede elementos com ResizeObserver, que o happy-dom não implementa por completo.
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver

config.global.plugins = [createVuetify(), i18n]
// O Monaco não roda no happy-dom: os editores viram um campo simples nos testes.
config.global.stubs = { CodeEditor: { props: ['modelValue'], template: '<textarea :value="modelValue" />' } }
