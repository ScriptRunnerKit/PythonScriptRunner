import { vi } from 'vitest'

type Reply = { status: number; body?: unknown; headers?: Record<string, string> }
type Handler = (request: Request) => Reply | Promise<Reply>

/** Substitui o fetch: `routes` mapeia "MÉTODO /caminho-da-api" para a resposta (ou uma função que a calcula). */
export function mockApi(routes: Record<string, Reply | Handler>) {
  const calls: Request[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(String(input), init)
    calls.push(request)
    const path = new URL(request.url).pathname.replace(/^.*\/api/, '')
    const route = routes[`${request.method} ${path}`]
    const reply = typeof route === 'function' ? await route(request) : (route ?? { status: 404 })
    return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), {
      status: reply.status,
      headers: { 'Content-Type': 'application/json', ...reply.headers },
    })
  })
  vi.stubGlobal('fetch', fetchMock)
  return { calls, fetch: fetchMock }
}
