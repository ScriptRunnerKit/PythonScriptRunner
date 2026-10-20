import type { Schemas } from '@/api/client'

/** Declaração de um campo no formato do manifesto (spec 003; data mapping §2.1). */
export type FieldDefinition = NonNullable<Schemas['StageDraftData']['inputs']>[number]
export type FieldKind = FieldDefinition['type']
export type ColumnKind = NonNullable<FieldDefinition['columns']>[number]['type']

/** Arquivo escolhido no formulário. */
export type FileValue = File | null

/**
 * Valor de um campo enquanto está no formulário: números e textos como digitados, data/hora local, JSON como texto,
 * arquivos como `File` e listas como arrays do valor do item.
 */
export type UiValue = string | boolean | FileValue | File[] | UiValue[] | null

/** Chave i18n de um problema de validação (com parâmetros). */
export interface ValueError {
  key: string
  params?: Record<string, unknown>
}

const integerPattern = /^-?\d+$/
const decimalPattern = /^-?\d+(\.\d+)?$/
const datePattern = /^\d{4}-\d{2}-\d{2}$/
const localDateTimePattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/

/** Tipo dos itens de uma lista (ou o próprio tipo). */
export function itemField(field: FieldDefinition): FieldDefinition {
  return { ...field, type: field.itemType ?? 'string', itemType: undefined, required: true, default: undefined }
}

/** Número digitado em pt-BR ou en: aceita vírgula ou ponto como separador decimal (sem separador de milhar). */
export function normalizeNumber(text: string): string {
  return text.trim().replace(',', '.')
}

function isEmpty(field: FieldDefinition, value: UiValue): boolean {
  switch (field.type) {
    case 'bool':
      return value === null
    case 'file':
      return value === null || value === undefined
    case 'list':
      return !Array.isArray(value) || value.length === 0
    default:
      return value === null || value === undefined || (typeof value === 'string' && value.trim() === '')
  }
}

/** Valor inicial do formulário (o default declarado, quando há). */
export function emptyValue(field: FieldDefinition): UiValue {
  if (field.default !== undefined && field.default !== null) {
    return fromWire(field, field.default)
  }

  switch (field.type) {
    case 'bool':
      return field.required === false ? null : false
    case 'file':
      return null
    case 'list':
      return []
    default:
      return ''
  }
}

/** Formata um instante para o seletor de data/hora local (`YYYY-MM-DDTHH:mm:ss`). */
export function toLocalInput(value: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}T${pad(value.getHours())}:${pad(value.getMinutes())}:${pad(value.getSeconds())}`
}

/** Data/hora local do seletor → ISO 8601 com o deslocamento do fuso do navegador naquela data. */
export function localToOffset(local: string): string | null {
  const match = localDateTimePattern.exec(local.trim())
  if (!match) {
    return null
  }

  const [, y, mo, d, h, mi, s] = match
  const date = new Date(Number(y), Number(mo) - 1, Number(d), Number(h), Number(mi), Number(s ?? '0'))
  if (Number.isNaN(date.getTime()) || date.getMonth() !== Number(mo) - 1 || date.getDate() !== Number(d)) {
    return null
  }

  const offset = -date.getTimezoneOffset()
  const sign = offset >= 0 ? '+' : '-'
  const hours = String(Math.floor(Math.abs(offset) / 60)).padStart(2, '0')
  const minutes = String(Math.abs(offset) % 60).padStart(2, '0')
  return `${toLocalInput(date)}${sign}${hours}:${minutes}`
}

function isValidDate(text: string): boolean {
  if (!datePattern.test(text)) {
    return false
  }

  const [y, m, d] = text.split('-').map(Number) as [number, number, number]
  const date = new Date(Date.UTC(y, m - 1, d))
  return date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d
}

function parseJson(text: string): { ok: true; value: unknown } | { ok: false } {
  try {
    return { ok: true, value: JSON.parse(text) }
  } catch {
    return { ok: false }
  }
}

function isRecordArray(value: unknown): value is Record<string, unknown>[] {
  return Array.isArray(value) && value.every((row) => row !== null && typeof row === 'object' && !Array.isArray(row))
}

function acceptedType(field: FieldDefinition, file: File): boolean {
  const accepted = field.contentTypes
  if (!accepted || accepted.length === 0) {
    return true
  }

  const type = file.type.split(';')[0]?.trim().toLowerCase() ?? ''
  return accepted.some((a) => a.toLowerCase() === type || (a.endsWith('/*') && type.startsWith(a.slice(0, -1).toLowerCase())))
}

/** Valida um valor do formulário; `null` quando está certo. */
export function validate(field: FieldDefinition, value: UiValue, maxFileBytes = 50 * 1024 * 1024): ValueError | null {
  if (isEmpty(field, value)) {
    return field.required === false || field.default !== undefined ? null : { key: 'fields.errors.required' }
  }

  switch (field.type) {
    case 'number': {
      const text = normalizeNumber(String(value))
      return Number.isFinite(Number(text)) && text !== '' && !/^[+-]?(infinity|nan)$/i.test(text)
        ? null
        : { key: 'fields.errors.number' }
    }
    case 'integer': {
      const text = normalizeNumber(String(value))
      return integerPattern.test(text) && Number.isSafeInteger(Number(text)) ? null : { key: 'fields.errors.integer' }
    }
    case 'decimal':
      return decimalPattern.test(normalizeNumber(String(value))) ? null : { key: 'fields.errors.decimal' }
    case 'string':
    case 'bool':
      return null
    case 'date':
      return isValidDate(String(value).trim()) ? null : { key: 'fields.errors.date' }
    case 'datetime':
      return localToOffset(String(value)) ? null : { key: 'fields.errors.datetime' }
    case 'enum':
      return (field.enumValues ?? []).includes(String(value)) ? null : { key: 'fields.errors.enum' }
    case 'json':
      return parseJson(String(value)).ok ? null : { key: 'fields.errors.json' }
    case 'table': {
      const parsed = parseJson(String(value))
      return parsed.ok && isRecordArray(parsed.value) ? null : { key: 'fields.errors.table' }
    }
    case 'file': {
      const file = value as File
      if (file.size > maxFileBytes) {
        return { key: 'fields.errors.fileSize', params: { mb: Math.round(maxFileBytes / (1024 * 1024)) } }
      }

      return acceptedType(field, file)
        ? null
        : { key: 'fields.errors.fileType', params: { types: field.contentTypes?.join(', ') } }
    }
    case 'list': {
      const item = itemField(field)
      const items = value as UiValue[]
      for (let i = 0; i < items.length; i++) {
        const error = validate(item, items[i] ?? null, maxFileBytes)
        if (error) {
          return { key: 'fields.errors.item', params: { index: i + 1, ...error.params, reason: error.key } }
        }
      }

      return null
    }
    default:
      return null
  }
}

/**
 * Converte o valor do formulário para o formato da API (wire; spec 003): `decimal` como texto, `datetime` com
 * deslocamento, `json`/`table` já interpretados. Arquivos não entram (vão como partes do multipart).
 * `undefined` quando o campo ficou vazio.
 */
export function toWire(field: FieldDefinition, value: UiValue): unknown {
  if (isEmpty(field, value)) {
    return undefined
  }

  switch (field.type) {
    case 'number':
    case 'integer':
      return Number(normalizeNumber(String(value)))
    case 'decimal': {
      const text = normalizeNumber(String(value))
      return text.startsWith('.') ? `0${text}` : text
    }
    case 'string':
    case 'enum':
      return String(value)
    case 'bool':
      return value === true
    case 'date':
      return String(value).trim()
    case 'datetime':
      return localToOffset(String(value)) ?? undefined
    case 'json':
    case 'table':
      return JSON.parse(String(value)) as unknown
    case 'file':
      return undefined
    case 'list': {
      const item = itemField(field)
      if (item.type === 'file') {
        return undefined
      }

      return (value as UiValue[]).map((v) => toWire(item, v))
    }
    default:
      return value
  }
}

/** Converte um valor da API (wire) para o formulário (defaults, entradas fixas de agendamento). */
export function fromWire(field: FieldDefinition, wire: unknown): UiValue {
  if (wire === null || wire === undefined) {
    return emptyValue({ ...field, default: undefined })
  }

  switch (field.type) {
    case 'number':
    case 'integer':
    case 'decimal':
    case 'string':
    case 'enum':
    case 'date':
      return String(wire)
    case 'bool':
      return wire === true
    case 'datetime': {
      const date = new Date(String(wire))
      return Number.isNaN(date.getTime()) ? String(wire) : toLocalInput(date)
    }
    case 'json':
    case 'table':
      return JSON.stringify(wire, null, 2)
    case 'list': {
      const item = itemField(field)
      return Array.isArray(wire) ? wire.map((v) => fromWire(item, v)) : []
    }
    default:
      return null
  }
}

/** Entradas de uma execução: os valores (wire) e os arquivos por campo. */
export interface RunInputs {
  inputs: Record<string, unknown>
  files: [string, File][]
}

/** Separa os valores do formulário em JSON e arquivos. */
export function collectInputs(fields: FieldDefinition[], values: Record<string, UiValue>): RunInputs {
  const inputs: Record<string, unknown> = {}
  const files: [string, File][] = []
  for (const field of fields) {
    const value = values[field.name] ?? null
    if (field.type === 'file' && value instanceof File) {
      files.push([field.name, value])
    } else if (field.type === 'list' && field.itemType === 'file' && Array.isArray(value)) {
      value.filter((v): v is File => v instanceof File).forEach((file) => files.push([field.name, file]))
    } else {
      const wire = toWire(field, value)
      if (wire !== undefined) {
        inputs[field.name] = wire
      }
    }
  }

  return { inputs, files }
}

/** Corpo multipart de um disparo ou teste (parte `inputs` em JSON + arquivos pelo nome do campo; spec 007). */
export function toFormData(run: RunInputs, extra: Record<string, string | undefined> = {}): FormData {
  const form = new FormData()
  // Texto, não Blob: um Blob vira arquivo ("blob") e o servidor o trataria como entrada do tipo file.
  form.append('inputs', JSON.stringify(run.inputs))
  for (const [name, file] of run.files) {
    form.append(name, file, file.name)
  }

  for (const [name, value] of Object.entries(extra)) {
    if (value !== undefined && value !== '') {
      form.append(name, value)
    }
  }

  return form
}

/** Lê um CSV (vírgula ou ponto e vírgula; aspas duplas) como linhas de uma tabela, tipando pelas colunas declaradas. */
export function parseCsv(text: string, columns?: FieldDefinition['columns']): Record<string, unknown>[] {
  const rows = splitCsv(text.replace(/^﻿/, ''))
  const header = rows.shift()?.map((h) => h.trim()) ?? []
  const types = new Map((columns ?? []).map((c) => [c.name, c.type]))
  return rows
    .filter((row) => row.some((cell) => cell.trim() !== ''))
    .map((row) => Object.fromEntries(header.map((name, i) => [name, typedCell(row[i] ?? '', types.get(name))])))
}

function typedCell(cell: string, type: ColumnKind | undefined): unknown {
  const text = cell.trim()
  switch (type) {
    case 'number':
    case 'integer': {
      const n = Number(normalizeNumber(text))
      return text === '' || Number.isNaN(n) ? text : n
    }
    case 'bool':
      return /^(true|sim|1)$/i.test(text) ? true : /^(false|não|nao|0)$/i.test(text) ? false : text
    case 'decimal':
      return normalizeNumber(text)
    default:
      return cell
  }
}

function splitCsv(text: string): string[][] {
  const delimiter = (text.split('\n')[0] ?? '').includes(';') ? ';' : ','
  const rows: string[][] = []
  let row: string[] = []
  let cell = ''
  let quoted = false
  for (let i = 0; i < text.length; i++) {
    const char = text[i]
    if (quoted) {
      if (char === '"' && text[i + 1] === '"') {
        cell += '"'
        i++
      } else if (char === '"') {
        quoted = false
      } else {
        cell += char
      }
    } else if (char === '"') {
      quoted = true
    } else if (char === delimiter) {
      row.push(cell)
      cell = ''
    } else if (char === '\n' || char === '\r') {
      if (char === '\r' && text[i + 1] === '\n') {
        i++
      }

      row.push(cell)
      rows.push(row)
      row = []
      cell = ''
    } else {
      cell += char
    }
  }

  if (cell !== '' || row.length > 0) {
    row.push(cell)
    rows.push(row)
  }

  return rows
}
