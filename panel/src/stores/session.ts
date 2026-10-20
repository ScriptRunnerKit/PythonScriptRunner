import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api, setAuthHooks, type AccessMode, type Role, type Schemas } from '@/api/client'
import { i18n } from '@/i18n'
import { useDrafts } from './drafts'
import { useNotify } from './notify'

const roles: Role[] = ['Reader', 'Executor', 'Author', 'Admin']

/** Posição de um papel (cumulativos; D-28). */
export function roleRank(role: Role | null | undefined): number {
  return role ? roles.indexOf(role) : -1
}

export type LoginResult = { ok: true } | { ok: false; reason: 'invalid' } | { ok: false; reason: 'throttled'; retryAfter: number }

type Navigate = (target: { name: string; query?: Record<string, string> }) => void

/**
 * Sessão do painel conforme o modo de acesso (spec 008 §0, ADR-0021): lê `/auth` e `/me`, faz login e logout do login
 * fixo, reage a 401 (diálogo de sessão expirada, redirecionamento ao host, página "sem acesso") e a 403 (D-152).
 */
export const useSession = defineStore('session', () => {
  const auth = ref<Schemas['AuthInfo'] | null>(null)
  const me = ref<Schemas['MeResponse'] | null>(null)
  const loaded = ref(false)
  /** O diálogo de sessão expirada está aberto (D-138). */
  const expired = ref(false)
  let waiters: ((ok: boolean) => void)[] = []
  let loading: Promise<void> | null = null
  let navigate: Navigate = () => {}
  let redirect: (url: string) => void = (url) => window.location.assign(url)

  const identified = computed(() => me.value !== null)
  const role = computed<Role | null>(() => me.value?.role ?? null)
  const mode = computed<AccessMode | null>(() => auth.value?.mode ?? null)
  /** Como o usuário atual foi identificado (o "Sair" depende disso; D-145). */
  const identifiedBy = computed<AccessMode | null>(() => me.value?.mode ?? null)
  const canLogout = computed(
    () => identifiedBy.value === 'FixedLogin' || (identifiedBy.value === 'Host' && Boolean(auth.value?.logoutUrl)),
  )

  function hasRole(minimum: Role): boolean {
    return roleRank(role.value) >= roleRank(minimum)
  }

  async function refreshMe() {
    const { data, response } = await api.GET('/me')
    me.value = response.ok && data ? data : null
  }

  function load(force = false): Promise<void> {
    if (loaded.value && !force) {
      return Promise.resolve()
    }

    loading ??= (async () => {
      try {
        const { data } = await api.GET('/auth')
        auth.value = data ?? null
        await refreshMe()
        useDrafts().purgeExpired()
        loaded.value = true
      } finally {
        loading = null
      }
    })()
    return loading
  }

  async function login(name: string, password: string): Promise<LoginResult> {
    const { response } = await api.POST('/auth/login', { body: { name, password } })
    if (response.status === 204) {
      await refreshMe()
      expired.value = false
      resolveWaiters(true)
      return { ok: true }
    }

    if (response.status === 429) {
      const seconds = Number.parseInt(response.headers.get('Retry-After') ?? '', 10)
      return { ok: false, reason: 'throttled', retryAfter: Number.isFinite(seconds) && seconds > 0 ? seconds : 60 }
    }

    return { ok: false, reason: 'invalid' }
  }

  async function logout() {
    useDrafts().flushAll()
    if (identifiedBy.value === 'Host' && auth.value?.logoutUrl) {
      redirect(auth.value.logoutUrl)
      return
    }

    await api.POST('/auth/logout')
    me.value = null
    navigate({ name: 'login' })
  }

  /** Sessão expirada: "Entrar como outro usuário" fecha o diálogo e vai para /entrar (D-151). */
  function switchUser() {
    useDrafts().flushAll()
    expired.value = false
    me.value = null
    resolveWaiters(false)
    navigate({ name: 'login' })
  }

  function resolveWaiters(ok: boolean) {
    const pending = waiters
    waiters = []
    pending.forEach((resolve) => resolve(ok))
  }

  function unauthorized(): Promise<boolean> {
    if (!identified.value && !expired.value) {
      return Promise.resolve(false)
    }

    switch (identifiedBy.value) {
      case 'FixedLogin':
        expired.value = true
        return new Promise((resolve) => waiters.push(resolve))
      case 'Host':
        useDrafts().flushAll()
        if (auth.value?.loginUrl) {
          redirect(auth.value.loginUrl)
          return new Promise(() => {})
        }

        me.value = null
        navigate({ name: 'no-access' })
        return Promise.resolve(false)
      default:
        me.value = null
        navigate({ name: 'no-access' })
        return Promise.resolve(false)
    }
  }

  async function forbidden(required: Role | null) {
    useDrafts().flushAll()
    await refreshMe()
    const t = i18n.global.t
    const notify = useNotify()
    if (required) {
      notify.warning(t('access.changed', { role: t(`roles.${required}`) }))
    } else {
      notify.warning(t('access.forbidden'))
    }
  }

  /** Liga a sessão ao roteador e ao cliente da API (chamado uma vez na inicialização). */
  function connect(options: { navigate: Navigate; redirect?: (url: string) => void }) {
    navigate = options.navigate
    if (options.redirect) {
      redirect = options.redirect
    }

    setAuthHooks({ unauthorized, forbidden: (required) => void forbidden(required) })
  }

  return {
    auth,
    me,
    loaded,
    expired,
    identified,
    role,
    mode,
    identifiedBy,
    canLogout,
    hasRole,
    load,
    refreshMe,
    login,
    logout,
    switchUser,
    connect,
  }
})
