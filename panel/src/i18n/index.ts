import { createI18n } from 'vue-i18n'
import ptBR from './pt-BR'

export type MessageSchema = typeof ptBR

/** Textos do painel (D-67): pt-BR; a estrutura já aceita inglês. */
export const i18n = createI18n<[MessageSchema], 'pt-BR'>({
  legacy: false,
  locale: 'pt-BR',
  fallbackLocale: 'pt-BR',
  messages: { 'pt-BR': ptBR },
  missingWarn: false,
  fallbackWarn: false,
})
