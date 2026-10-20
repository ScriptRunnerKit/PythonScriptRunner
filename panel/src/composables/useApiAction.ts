import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/api/client'
import { useNotify } from '@/stores/notify'

/** Mensagem legível de um erro da API (código estável → texto do i18n, ou o detalhe do servidor). */
export function useErrorText() {
  const { t, te } = useI18n()
  return (error: unknown): string => {
    if (error instanceof ApiError) {
      const key = `errors.${error.code}`
      if (te(key)) {
        return t(key)
      }

      return error.message || t('errors.generic')
    }

    return t('errors.generic')
  }
}

/** Executa uma ação da API com indicador de carregamento e aviso de erro (401/403 já tratados pela sessão). */
export function useApiAction() {
  const busy = ref(false)
  const notify = useNotify()
  const text = useErrorText()

  async function run<T>(action: () => Promise<T>, options: { silentCodes?: string[] } = {}): Promise<T | undefined> {
    busy.value = true
    try {
      return await action()
    } catch (error) {
      if (error instanceof ApiError && options.silentCodes?.includes(error.code)) {
        throw error
      }

      // 401 e 403 já foram tratados pela sessão (diálogo, redirecionamento ou aviso "seu acesso mudou").
      if (!(error instanceof ApiError && (error.status === 401 || error.status === 403))) {
        notify.error(text(error))
      }

      return undefined
    } finally {
      busy.value = false
    }
  }

  return { busy, run }
}
