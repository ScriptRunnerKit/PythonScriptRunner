import type { Schemas } from '@/api/client'

const locale = 'pt-BR'
const dateTime = new Intl.DateTimeFormat(locale, { dateStyle: 'short', timeStyle: 'medium' })
const dateOnly = new Intl.DateTimeFormat(locale, { dateStyle: 'short' })
const utc = new Intl.DateTimeFormat(locale, { dateStyle: 'short', timeStyle: 'medium', timeZone: 'UTC' })
const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 10 })

/** Data e hora local (Intl; spec 008). */
export function formatDateTime(value: string | Date | null | undefined): string {
  if (!value) {
    return '—'
  }

  const date = typeof value === 'string' ? new Date(value) : value
  return Number.isNaN(date.getTime()) ? String(value) : dateTime.format(date)
}

/** Mesma data em UTC (tooltip das datas). */
export function formatUtc(value: string | Date | null | undefined): string {
  if (!value) {
    return ''
  }

  const date = typeof value === 'string' ? new Date(value) : value
  return Number.isNaN(date.getTime()) ? '' : `${utc.format(date)} UTC`
}

export function formatDate(value: string | null | undefined): string {
  if (!value) {
    return '—'
  }

  // Datas sem hora (YYYY-MM-DD) são do calendário, sem fuso.
  const [y, m, d] = value.split('-').map(Number)
  return y && m && d ? dateOnly.format(new Date(y, m - 1, d)) : value
}

export function formatNumber(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === '') {
    return '—'
  }

  const n = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(n) ? number.format(n) : String(value)
}

/** Decimal mantido como texto: só troca o separador para pt-BR, sem arredondar. */
export function formatDecimal(value: string | number | null | undefined): string {
  if (value === null || value === undefined) {
    return '—'
  }

  const [integer = '', fraction] = String(value).split('.')
  const grouped = Number.isFinite(Number(integer)) ? number.format(Number(integer)) : integer
  return fraction === undefined ? grouped : `${integer === '-0' ? '-0' : grouped},${fraction}`
}

/** Duração legível (1 h 02 min, 3,2 s, 120 ms). */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) {
    return '—'
  }

  if (ms < 1000) {
    return `${Math.round(ms)} ms`
  }

  const seconds = ms / 1000
  if (seconds < 60) {
    return `${seconds.toLocaleString(locale, { maximumFractionDigits: 1 })} s`
  }

  const minutes = Math.floor(seconds / 60)
  const rest = Math.floor(seconds % 60)
  if (minutes < 60) {
    return `${minutes} min ${String(rest).padStart(2, '0')} s`
  }

  return `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')} min`
}

/** Duração de "HH:MM:SS.fffffff" (TimeSpan da API) em milissegundos. */
export function timeSpanMs(value: string | null | undefined): number | null {
  if (!value) {
    return null
  }

  const match = /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(value)
  if (!match) {
    return null
  }

  const [, sign, days, h, m, s, fraction] = match
  const ms =
    (Number(days ?? 0) * 86400 + Number(h) * 3600 + Number(m) * 60 + Number(s)) * 1000 + Number(`0.${fraction ?? '0'}`) * 1000
  return sign ? -ms : ms
}

/** Milissegundos → TimeSpan da API ("HH:MM:SS"). */
export function toTimeSpan(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000))
  const days = Math.floor(total / 86400)
  const pad = (n: number) => String(n).padStart(2, '0')
  const body = `${pad(Math.floor((total % 86400) / 3600))}:${pad(Math.floor((total % 3600) / 60))}:${pad(total % 60)}`
  return days > 0 ? `${days}.${body}` : body
}

export function elapsedMs(start: string | null | undefined, end: string | null | undefined, now = Date.now()): number | null {
  if (!start) {
    return null
  }

  const from = new Date(start).getTime()
  const to = end ? new Date(end).getTime() : now
  return Number.isNaN(from) || Number.isNaN(to) ? null : Math.max(0, to - from)
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) {
    return '—'
  }

  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }

  return `${value.toLocaleString(locale, { maximumFractionDigits: unit === 0 ? 0 : 1 })} ${units[unit]}`
}

type RunStatus = Schemas['RunStatus']
type StepStatus = Schemas['StepStatus']

/** Cor de um status de execução ou passo (canvas, chips). */
export function statusColor(
  status: RunStatus | StepStatus | Schemas['AttemptStatus'] | Schemas['VersionStatus'] | string,
): string {
  switch (status) {
    case 'Succeeded':
    case 'Published':
      return 'success'
    case 'Failed':
    case 'TimedOut':
    case 'WorkerLost':
      return 'error'
    case 'Running':
    case 'Cancelling':
      return 'info'
    case 'Queued':
    case 'Pending':
    case 'Draft':
      return 'warning'
    default:
      return 'secondary'
  }
}

export function statusIcon(status: string): string {
  switch (status) {
    case 'Succeeded':
      return 'mdi-check-circle'
    case 'Failed':
    case 'TimedOut':
    case 'WorkerLost':
      return 'mdi-alert-circle'
    case 'Running':
      return 'mdi-progress-clock'
    case 'Cancelling':
    case 'Cancelled':
      return 'mdi-cancel'
    case 'Queued':
    case 'Pending':
      return 'mdi-timer-sand'
    case 'Skipped':
      return 'mdi-skip-next-circle-outline'
    default:
      return 'mdi-circle-outline'
  }
}

/** Execução ainda em andamento (duração atualizada localmente; spec 008). */
export function isActive(status: string): boolean {
  return status === 'Pending' || status === 'Queued' || status === 'Running' || status === 'Cancelling'
}
