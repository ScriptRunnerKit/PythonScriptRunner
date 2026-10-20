import { createRouter, createWebHistory, type RouteLocationNormalized, type RouteRecordRaw } from 'vue-router'
import { basePath, type Role } from '@/api/client'
import { useSession } from '@/stores/session'
import AppShell from '@/components/layout/AppShell.vue'

declare module 'vue-router' {
  interface RouteMeta {
    /** Página sem login (entrar, sem acesso). */
    public?: boolean
    /** Papel mínimo (cumulativo; D-28). */
    role?: Role
    /** Chave i18n do título. */
    title?: string
  }
}

export const routes: RouteRecordRaw[] = [
  {
    path: '/entrar',
    name: 'login',
    component: () => import('@/views/LoginView.vue'),
    meta: { public: true, title: 'auth.login.title' },
  },
  {
    path: '/sem-acesso',
    name: 'no-access',
    component: () => import('@/views/NoAccessView.vue'),
    meta: { public: true, title: 'auth.noAccess.title' },
  },
  {
    path: '/sem-papel',
    name: 'no-role',
    component: () => import('@/views/NoRoleView.vue'),
    meta: { public: true, title: 'auth.noRole.title' },
  },
  {
    path: '/',
    component: AppShell,
    children: [
      {
        path: '',
        name: 'overview',
        component: () => import('@/views/OverviewView.vue'),
        meta: { role: 'Reader', title: 'nav.overview' },
      },
      {
        path: 'etapas',
        name: 'stages',
        component: () => import('@/views/stages/StageListView.vue'),
        meta: { role: 'Reader', title: 'nav.stages' },
      },
      {
        path: 'etapas/:key',
        name: 'stage',
        component: () => import('@/views/stages/StageDetailView.vue'),
        meta: { role: 'Reader', title: 'nav.stages' },
      },
      {
        path: 'etapas/:key/editar',
        name: 'stage-editor',
        component: () => import('@/views/stages/StageEditorView.vue'),
        meta: { role: 'Author', title: 'stages.editor.title' },
      },
      {
        path: 'trilhas',
        name: 'pipelines',
        component: () => import('@/views/pipelines/PipelineListView.vue'),
        meta: { role: 'Reader', title: 'nav.pipelines' },
      },
      {
        path: 'trilhas/:key',
        name: 'pipeline',
        component: () => import('@/views/pipelines/PipelineDetailView.vue'),
        meta: { role: 'Reader', title: 'nav.pipelines' },
      },
      {
        path: 'trilhas/:key/editar',
        name: 'pipeline-editor',
        component: () => import('@/views/pipelines/PipelineEditorView.vue'),
        meta: { role: 'Author', title: 'pipelines.editor.title' },
      },
      {
        path: 'execucoes',
        name: 'runs',
        component: () => import('@/views/runs/RunListView.vue'),
        meta: { role: 'Reader', title: 'nav.runs' },
      },
      {
        path: 'execucoes/:id',
        name: 'run',
        component: () => import('@/views/runs/RunDetailView.vue'),
        meta: { role: 'Reader', title: 'nav.runs' },
      },
      {
        path: 'agendamentos',
        name: 'schedules',
        component: () => import('@/views/schedules/ScheduleListView.vue'),
        meta: { role: 'Reader', title: 'nav.schedules' },
      },
      {
        path: 'auditoria',
        name: 'audit',
        component: () => import('@/views/audit/AuditView.vue'),
        meta: { role: 'Admin', title: 'nav.audit' },
      },
      {
        path: 'administracao',
        name: 'admin',
        component: () => import('@/views/admin/AdminView.vue'),
        meta: { role: 'Admin', title: 'nav.admin' },
      },
      {
        path: 'sem-permissao',
        name: 'forbidden',
        component: () => import('@/views/ForbiddenView.vue'),
        meta: { title: 'auth.forbidden.title' },
      },
      {
        path: ':pathMatch(.*)*',
        name: 'not-found',
        component: () => import('@/views/NotFoundView.vue'),
        meta: { title: 'notFound.title' },
      },
    ],
  },
]

/** Destino interno seguro para "voltar" depois do login (sem redirecionamento aberto). */
export function safeReturn(value: unknown): string {
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : '/'
}

/**
 * Entrada conforme o modo de acesso (spec 008 §0): login fixo → /entrar; host → loginUrl; filtro ou local negado →
 * "sem acesso"; host sem papel → "sem papel"; rota acima do papel → "sem permissão".
 */
export async function guard(to: RouteLocationNormalized) {
  const session = useSession()
  await session.load()
  if (to.meta.public) {
    if (to.name === 'login' && (session.identified || session.mode !== 'FixedLogin')) {
      return session.identified ? safeReturn(to.query.volta) : { name: 'overview' }
    }

    return true
  }

  if (!session.identified) {
    switch (session.mode) {
      case 'FixedLogin':
        return { name: 'login', query: to.fullPath === '/' ? {} : { volta: to.fullPath } }
      case 'Host':
        if (session.auth?.loginUrl) {
          window.location.assign(session.auth.loginUrl)
          return false
        }

        return { name: 'no-access' }
      default:
        return { name: 'no-access' }
    }
  }

  if (session.role === null) {
    return { name: 'no-role' }
  }

  if (to.meta.role && !session.hasRole(to.meta.role)) {
    return { name: 'forbidden', query: { rota: to.fullPath } }
  }

  return true
}

export function createAppRouter() {
  const router = createRouter({ history: createWebHistory(basePath), routes })
  router.beforeEach(guard)
  return router
}
