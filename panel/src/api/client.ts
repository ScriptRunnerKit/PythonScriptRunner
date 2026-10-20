import createClient from 'openapi-fetch'
import type { components, paths } from './schema'

/** Esquemas da API do painel (spec 007), gerados do snapshot do OpenAPI. */
export type Schemas = components['schemas']
export type Role = Schemas['DashboardRole']
export type AccessMode = Schemas['DashboardAccessMode']

/** Raiz do painel lida de <base href> (o pacote injeta o prefixo configurado; spec 007, AC-14). */
export const basePath = new URL(document.baseURI).pathname.replace(/\/?$/, '/')
export const apiBase = `${basePath}api`
export const hubUrl = `${basePath}hubs/runs`

/** Erro da API no formato ProblemDetails, com o `code` estável da spec 007. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly problem: Record<string, unknown>,
  ) {
    super(typeof problem.detail === 'string' ? problem.detail : typeof problem.title === 'string' ? problem.title : code)
    this.name = 'ApiError'
  }

  /** Valor de uma extensão do ProblemDetails (ex.: `current`, `errors`, `line`). */
  extension<T>(name: string): T | undefined {
    return this.problem[name] as T | undefined
  }
}

/** Reações da sessão a 401/403 (registradas pela store de sessão; evita dependência circular). */
export interface AuthHooks {
  /** Sessão perdida no meio do uso; devolve `true` quando o usuário entrou de novo e a chamada pode ser refeita. */
  unauthorized(): Promise<boolean>
  /** Ação negada: o papel pode ter mudado (D-152). */
  forbidden(required: Role | null): void
}

let hooks: AuthHooks | null = null

export function setAuthHooks(value: AuthHooks | null) {
  hooks = value
}

const roleRules: [string, RegExp, Role][] = [
  ['DELETE', /^\/(stages|pipelines)\/[^/]+$/, 'Admin'],
  ['PATCH', /^\/pipelines\/[^/]+\/retention$/, 'Admin'],
  ['*', /^\/(audit|admin\/)/, 'Admin'],
  ['POST', /^\/(stages|pipelines)\/[^/]+\/runs$/, 'Executor'],
  ['POST', /^\/runs\/[^/]+\/(cancel|reprocess)$/, 'Executor'],
  ['POST', /^\/schedules\/preview$/, 'Reader'],
  ['GET', /^\/(stages|pipelines)\/[^/]+\/draft$/, 'Author'],
  ['GET', /./, 'Reader'],
  ['*', /./, 'Author'],
]

/** Papel exigido por uma chamada (mesma matriz do servidor; usado na mensagem "seu acesso mudou"). */
export function requiredRole(method: string, path: string): Role | null {
  if (path.startsWith('/auth') || path === '/me') {
    return null
  }

  const rule = roleRules.find(([m, pattern]) => (m === '*' || m === method) && pattern.test(path))
  return rule ? rule[2] : null
}

function apiPath(url: string): string {
  const pathname = new URL(url, document.baseURI).pathname
  return pathname.startsWith(apiBase) ? pathname.slice(apiBase.length) : pathname
}

/**
 * Envia uma requisição da API: `X-PSR-Request: 1` nas mutantes (CSRF; spec 007) e, em 401 no meio do uso, espera o
 * novo login e refaz a chamada (D-138).
 */
export async function send(input: Request): Promise<Response> {
  const request = new Request(input, { credentials: 'same-origin' })
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    request.headers.set('X-PSR-Request', '1')
  }

  const path = apiPath(request.url)
  const retry = request.clone()
  const response = await fetch(request)
  if (response.status === 401 && hooks && !path.startsWith('/auth')) {
    return (await hooks.unauthorized()) ? fetch(retry) : response
  }

  if (response.status === 403 && hooks) {
    hooks.forbidden(requiredRole(request.method, path))
  }

  return response
}

/** Requisição direta (multipart, downloads, texto) com o mesmo tratamento de sessão. */
export function apiFetch(path: string, init?: RequestInit): Promise<Response> {
  return send(new Request(`${apiBase}${path}`, init))
}

export const api = createClient<paths>({ baseUrl: apiBase, fetch: send })

/** Converte uma resposta de erro em {@link ApiError}. */
export async function toApiError(response: Response, body?: unknown): Promise<ApiError> {
  let problem = body as Record<string, unknown> | undefined
  if (problem === undefined) {
    try {
      problem = (await response.clone().json()) as Record<string, unknown>
    } catch {
      problem = {}
    }
  }

  const code = typeof problem?.code === 'string' ? problem.code : `HTTP_${response.status}`
  return new ApiError(response.status, code, problem ?? {})
}

interface FetchResult<T> {
  data?: T
  error?: unknown
  response: Response
}

/** Devolve os dados de uma chamada do cliente tipado ou lança {@link ApiError}. */
export async function unwrap<T>(call: Promise<FetchResult<T>>): Promise<T> {
  const { data, error, response } = await call
  if (!response.ok) {
    throw await toApiError(response, error)
  }

  return data as T
}

/** Como {@link unwrap}, devolvendo também o ETag (concorrência otimista; spec 007). */
export async function unwrapWithETag<T>(call: Promise<FetchResult<T>>): Promise<{ data: T; etag: string }> {
  const { data, error, response } = await call
  if (!response.ok) {
    throw await toApiError(response, error)
  }

  return { data: data as T, etag: response.headers.get('ETag') ?? '' }
}

/** Lança {@link ApiError} quando a resposta direta falhou. */
export async function ensureOk(response: Response): Promise<Response> {
  if (!response.ok) {
    throw await toApiError(response)
  }

  return response
}

/** Endereço de download de um arquivo de execução. */
export function artifactUrl(id: string): string {
  return `${apiBase}/artifacts/${id}`
}
