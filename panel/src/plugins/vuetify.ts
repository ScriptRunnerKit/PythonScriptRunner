import '@fontsource/roboto/400.css'
import '@fontsource/roboto/500.css'
import '@fontsource/roboto/700.css'
import '@mdi/font/css/materialdesignicons.css'
import 'vuetify/styles'
import { createVuetify } from 'vuetify'
import { pt } from 'vuetify/locale'

/** Vuetify 4 (D-159) com os temas claro e escuro do painel; fontes e ícones empacotados (CSP `'self'`). */
export const vuetify = createVuetify({
  locale: { locale: 'pt', messages: { pt } },
  theme: {
    defaultTheme: 'light',
    themes: {
      light: {
        dark: false,
        colors: {
          primary: '#1e5aa8',
          secondary: '#3c6e71',
          success: '#2e7d32',
          warning: '#b26a00',
          error: '#c62828',
          info: '#0277bd',
        },
        // 0,60 (padrão) deixa rótulos e textos secundários em 4,3:1 sobre branco; WCAG AA pede 4,5:1 (spec 008, AC-15).
        variables: { 'medium-emphasis-opacity': 0.7 },
      },
      dark: {
        dark: true,
        colors: {
          primary: '#7fb0ff',
          secondary: '#8fc1c3',
          success: '#81c784',
          warning: '#ffb74d',
          error: '#ef9a9a',
          info: '#81d4fa',
        },
      },
    },
  },
  defaults: {
    VBtn: { variant: 'flat' },
    VTextField: { variant: 'outlined', density: 'comfortable' },
    VTextarea: { variant: 'outlined', density: 'comfortable' },
    VSelect: { variant: 'outlined', density: 'comfortable' },
    VAutocomplete: { variant: 'outlined', density: 'comfortable' },
    VCombobox: { variant: 'outlined', density: 'comfortable' },
    VFileInput: { variant: 'outlined', density: 'comfortable' },
  },
})
