import { onBeforeUnmount, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useDrafts, type LocalDraft } from '@/stores/drafts'
import { useSession } from '@/stores/session'

/**
 * Rascunho local de um editor (QA-008-4, D-143): oferece restaurar ao abrir, grava ao sair com alterações não salvas
 * (inclusive no redirecionamento ao login do host) e apaga depois de salvar com sucesso.
 */
export function useLocalDraft<T>(entity: () => string, current: () => T | undefined) {
  const drafts = useDrafts()
  const session = useSession()
  const { t } = useI18n()
  const offer = ref<LocalDraft<T> | null>(null)
  const subject = () => session.me?.subject ?? 'anonymous'
  const unregister = drafts.register(subject(), entity(), current)

  function check() {
    offer.value = drafts.load<T>(subject(), entity())
  }

  function discard() {
    drafts.remove(subject(), entity())
    offer.value = null
  }

  /** Alterações salvas no servidor: o rascunho local deixa de existir. */
  function saved() {
    drafts.remove(subject(), entity())
    offer.value = null
  }

  function keep() {
    const data = current()
    if (data !== undefined) {
      drafts.save(subject(), entity(), data)
    }
  }

  function onUnload(event: BeforeUnloadEvent) {
    if (current() !== undefined) {
      keep()
      event.preventDefault()
    }
  }

  window.addEventListener('beforeunload', onUnload)
  onBeforeUnmount(() => {
    window.removeEventListener('beforeunload', onUnload)
    unregister()
  })
  onBeforeRouteLeave(() => {
    if (current() === undefined) {
      return true
    }

    // Alterações não salvas: confirma e preserva o rascunho local (spec 008, casos de borda).
    keep()
    return window.confirm(t('drafts.leave'))
  })

  return { offer, check, discard, saved, keep }
}
