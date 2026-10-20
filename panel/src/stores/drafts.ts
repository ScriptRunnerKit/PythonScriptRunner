import { defineStore } from 'pinia'

const prefix = 'psr.draft:'
/** Rascunhos locais valem 7 dias (D-143). */
export const draftLifetimeMs = 7 * 24 * 60 * 60 * 1000

interface StoredDraft<T> {
  savedAt: number
  data: T
}

/** Um rascunho local oferecido para restaurar. */
export interface LocalDraft<T> {
  savedAt: Date
  data: T
}

type Provider = () => unknown

/**
 * Alterações não salvas guardadas no navegador (QA-008-4, D-143): `localStorage`, chave por usuário e entidade,
 * apagadas ao salvar ou depois de 7 dias. Editores abertos se registram para que a sessão grave tudo antes de sair
 * (ex.: redirecionamento ao login do host).
 */
export const useDrafts = defineStore('drafts', () => {
  const providers = new Map<string, { subject: string; provide: Provider }>()
  let clock: () => number = () => Date.now()

  function key(subject: string, entity: string) {
    return `${prefix}${subject}|${entity}`
  }

  function storage(): Storage | null {
    try {
      return window.localStorage
    } catch {
      return null
    }
  }

  function save<T>(subject: string, entity: string, data: T) {
    try {
      storage()?.setItem(key(subject, entity), JSON.stringify({ savedAt: clock(), data } satisfies StoredDraft<T>))
    } catch {
      // Sem espaço ou sem armazenamento: o rascunho local é uma garantia extra, não uma obrigação.
    }
  }

  function load<T>(subject: string, entity: string): LocalDraft<T> | null {
    const store = storage()
    const text = store?.getItem(key(subject, entity))
    if (!text) {
      return null
    }

    try {
      const stored = JSON.parse(text) as StoredDraft<T>
      if (clock() - stored.savedAt > draftLifetimeMs) {
        store?.removeItem(key(subject, entity))
        return null
      }

      return { savedAt: new Date(stored.savedAt), data: stored.data }
    } catch {
      store?.removeItem(key(subject, entity))
      return null
    }
  }

  function remove(subject: string, entity: string) {
    storage()?.removeItem(key(subject, entity))
  }

  /** Apaga os rascunhos vencidos de todos os usuários (na abertura do painel). */
  function purgeExpired() {
    const store = storage()
    if (!store) {
      return
    }

    for (let i = store.length - 1; i >= 0; i--) {
      const name = store.key(i)
      if (!name?.startsWith(prefix)) {
        continue
      }

      try {
        const stored = JSON.parse(store.getItem(name) ?? '') as StoredDraft<unknown>
        if (clock() - stored.savedAt > draftLifetimeMs) {
          store.removeItem(name)
        }
      } catch {
        store.removeItem(name)
      }
    }
  }

  /** Um editor aberto informa como obter suas alterações não salvas (`undefined` quando não há). */
  function register(subject: string, entity: string, provide: Provider): () => void {
    providers.set(entity, { subject, provide })
    return () => providers.delete(entity)
  }

  /** Grava agora as alterações não salvas de todos os editores abertos. */
  function flushAll() {
    for (const [entity, { subject, provide }] of providers) {
      const data = provide()
      if (data !== undefined) {
        save(subject, entity, data)
      }
    }
  }

  /** Relógio dos testes (AC-22). */
  function useClock(now: () => number) {
    clock = now
  }

  return { save, load, remove, purgeExpired, register, flushAll, useClock }
})
