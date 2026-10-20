import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { RouteLocationNormalized } from 'vue-router'
import { setAuthHooks } from '@/api/client'
import { mockApi } from '@/test/api-mock'
import { useDrafts } from '@/stores/drafts'
import { useSession } from '@/stores/session'
import { guard, safeReturn } from '.'

const route = (path: string, meta: Record<string, unknown> = {}, name?: string) =>
  ({ path, fullPath: path, meta, name, query: {} }) as unknown as RouteLocationNormalized

function auth(mode: string, extra: Record<string, unknown> = {}) {
  return { status: 200, body: { mode, loginUrl: null, logoutUrl: null, localRequests: false, ...extra } }
}

describe('Entrada no painel conforme o modo de acesso (spec 008 §0)', () => {
  const assign = vi.fn()

  beforeEach(() => {
    setAuthHooks(null)
    setActivePinia(createPinia())
    window.localStorage.clear()
    vi.stubGlobal('location', { ...window.location, assign })
    assign.mockReset()
  })

  afterEach(() => vi.unstubAllGlobals())

  it('Spec008_AC17 login fixo sem sessão vai para /entrar e volta à rota pedida', async () => {
    mockApi({ 'GET /auth': auth('FixedLogin'), 'GET /me': { status: 401 } })

    expect(await guard(route('/execucoes', { role: 'Reader' }))).toEqual({ name: 'login', query: { volta: '/execucoes' } })
    expect(safeReturn('/execucoes')).toBe('/execucoes')
    expect(safeReturn('//malicioso.com')).toBe('/')
  })

  it('Spec008_AC20 host sem sessão grava o rascunho local e vai para a loginUrl', async () => {
    mockApi({ 'GET /auth': auth('Host', { loginUrl: '/conta/entrar?returnUrl=%2Fpsr%2F' }), 'GET /me': { status: 401 } })
    const session = useSession()
    session.connect({ navigate: vi.fn(), redirect: assign })

    expect(await guard(route('/etapas', { role: 'Reader' }))).toBe(false)
    expect(assign).toHaveBeenCalledWith('/conta/entrar?returnUrl=%2Fpsr%2F')
  })

  it('Spec008_AC20 host: sessão perdida no meio do uso grava o rascunho e redireciona', async () => {
    mockApi({
      'GET /auth': auth('Host', { loginUrl: '/conta/entrar', logoutUrl: '/conta/sair' }),
      'GET /me': {
        status: 200,
        body: { subject: 'sub-1', name: 'Ana', role: 'Author', roles: ['Reader', 'Executor', 'Author'], mode: 'Host' },
      },
      'GET /stages': { status: 401 },
    })
    const session = useSession()
    const navigate = vi.fn()
    session.connect({ navigate, redirect: assign })
    await session.load()
    useDrafts().register('sub-1', 'stage:eco', () => ({ script: 'não salvo' }))

    const { api } = await import('@/api/client')
    void api.GET('/stages')
    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith('/conta/entrar'))

    expect(useDrafts().load('sub-1', 'stage:eco')?.data).toEqual({ script: 'não salvo' })
    expect(session.canLogout).toBe(true)
  })

  it('Spec008_AC20 host com /me sem papel mostra "sem papel"; Sair só com logoutUrl', async () => {
    mockApi({
      'GET /auth': auth('Host', { loginUrl: '/conta/entrar' }),
      'GET /me': { status: 200, body: { subject: 'sub-2', name: 'Bia', role: null, roles: [], mode: 'Host' } },
    })

    expect(await guard(route('/', { role: 'Reader' }))).toEqual({ name: 'no-role' })
    expect(useSession().canLogout, 'sem logoutUrl não há Sair').toBe(false)
  })

  it('Spec008_AC21 filtro que nega mostra "sem acesso" (sem login)', async () => {
    mockApi({ 'GET /auth': auth('Filter'), 'GET /me': { status: 401 } })

    expect(await guard(route('/trilhas', { role: 'Reader' }))).toEqual({ name: 'no-access' })
    expect(await guard(route('/entrar', { public: true }, 'login')), 'não existe /entrar fora do login fixo').toEqual({
      name: 'overview',
    })
  })

  it('Spec008_AC21 somente local entra como Admin e sem botão Sair', async () => {
    mockApi({
      'GET /auth': auth('Localhost', { localRequests: true }),
      'GET /me': {
        status: 200,
        body: {
          subject: 'local',
          name: 'local',
          role: 'Admin',
          roles: ['Reader', 'Executor', 'Author', 'Admin'],
          mode: 'Localhost',
        },
      },
    })

    expect(await guard(route('/auditoria', { role: 'Admin' }))).toBe(true)
    expect(useSession().canLogout).toBe(false)
  })

  it('Spec008_AC10 rota acima do papel mostra "sem permissão"', async () => {
    mockApi({
      'GET /auth': auth('FixedLogin'),
      'GET /me': {
        status: 200,
        body: { subject: 'login:leo', name: 'leo', role: 'Reader', roles: ['Reader'], mode: 'FixedLogin' },
      },
    })

    expect(await guard(route('/etapas/eco/editar', { role: 'Author' }))).toEqual({
      name: 'forbidden',
      query: { rota: '/etapas/eco/editar' },
    })
    expect(useSession().canLogout).toBe(true)
  })
})
