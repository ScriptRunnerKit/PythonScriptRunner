import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, setAuthHooks } from '@/api/client'
import { mockApi } from '@/test/api-mock'
import { useNotify } from './notify'
import { useSession } from './session'

const me = (role: string | null) => ({
  status: 200,
  body: { subject: 'login:ana', name: 'ana', role, roles: [], mode: 'FixedLogin' },
})

describe('Sessão do painel', () => {
  beforeEach(() => {
    setAuthHooks(null)
    setActivePinia(createPinia())
  })
  afterEach(() => vi.unstubAllGlobals())

  it('Spec008_AC24 403 relê /me, esconde o que não vale e avisa "seu acesso mudou"', async () => {
    let role = 'Author'
    mockApi({
      'GET /auth': { status: 200, body: { mode: 'FixedLogin', localRequests: false } },
      'GET /me': () => me(role),
      'POST /stages': { status: 403, body: { code: 'FORBIDDEN' } },
    })
    const session = useSession()
    session.connect({ navigate: vi.fn() })
    await session.load()
    expect(session.hasRole('Author')).toBe(true)

    role = 'Reader'
    await api.POST('/stages', { body: { key: 'x', name: 'x' } })

    await vi.waitFor(() => expect(session.role).toBe('Reader'))
    expect(session.hasRole('Author')).toBe(false)
    expect(useNotify().current?.text).toBe('Seu acesso mudou: a ação requer papel Autor.')
  })

  it('Spec008_AC19 sessão expirada: o diálogo pede o mesmo usuário e a chamada é refeita depois do login', async () => {
    let logged = true
    const { calls } = mockApi({
      'GET /auth': { status: 200, body: { mode: 'FixedLogin', localRequests: false } },
      'GET /me': () => (logged ? me('Author') : { status: 401 }),
      'PUT /stages/eco/draft': () => (logged ? { status: 200, body: { key: 'eco' } } : { status: 401 }),
      'POST /auth/login': () => {
        logged = true
        return { status: 204 }
      },
    })
    const session = useSession()
    session.connect({ navigate: vi.fn() })
    await session.load()

    logged = false
    const save = api.PUT('/stages/{key}/draft', {
      params: { path: { key: 'eco' } },
      body: { script: 'x', pythonVersion: '3.12' },
      headers: { 'If-Match': '"1"' },
    })
    await vi.waitFor(() => expect(session.expired).toBe(true))
    expect(session.me?.name, 'o nome do diálogo é o do usuário da sessão').toBe('ana')

    expect(await session.login('ana', 'senha')).toEqual({ ok: true })
    const { response } = await save

    expect(response.status).toBe(200)
    expect(session.expired).toBe(false)
    const puts = calls.filter((c) => c.method === 'PUT')
    expect(puts).toHaveLength(2)
    expect(puts[1]!.headers.get('X-PSR-Request')).toBe('1')
    expect(puts[1]!.headers.get('If-Match')).toBe('"1"')
  })

  it('Spec008_AC18 429 devolve o tempo de espera do Retry-After', async () => {
    mockApi({ 'POST /auth/login': { status: 429, headers: { 'Retry-After': '42' } } })

    expect(await useSession().login('ana', 'x')).toEqual({ ok: false, reason: 'throttled', retryAfter: 42 })
  })
})
