import { describe, expect, it } from 'vitest'
import {
  collectInputs,
  emptyValue,
  fromWire,
  localToOffset,
  parseCsv,
  toFormData,
  toWire,
  validate,
  type FieldDefinition,
} from './values'

/** Spec 008, AC-14: os widgets dos 12 tipos validam e serializam conforme o mapeamento (decimal como texto, datetime com deslocamento). */
const field = (type: FieldDefinition['type'], extra: Partial<FieldDefinition> = {}): FieldDefinition => ({
  name: 'campo',
  type,
  ...extra,
})

describe('Spec008_AC14 widgets dos 12 tipos', () => {
  it('number aceita vírgula ou ponto e vira número', () => {
    expect(validate(field('number'), '1,5')).toBeNull()
    expect(toWire(field('number'), '1,5')).toBe(1.5)
    expect(validate(field('number'), 'abc')?.key).toBe('fields.errors.number')
  })

  it('integer recusa fração', () => {
    expect(toWire(field('integer'), '42')).toBe(42)
    expect(validate(field('integer'), '4.2')?.key).toBe('fields.errors.integer')
  })

  it('decimal fica como texto, sem arredondar', () => {
    expect(toWire(field('decimal'), '0,1000000000000000055511')).toBe('0.1000000000000000055511')
    expect(toWire(field('decimal'), ',5')).toBe('0.5')
    expect(validate(field('decimal'), '1.2.3')?.key).toBe('fields.errors.decimal')
  })

  it('string e bool', () => {
    expect(toWire(field('string'), 'olá')).toBe('olá')
    expect(toWire(field('bool'), true)).toBe(true)
    expect(toWire(field('bool'), false)).toBe(false)
  })

  it('date valida o calendário', () => {
    expect(toWire(field('date'), '2026-02-28')).toBe('2026-02-28')
    expect(validate(field('date'), '2026-02-30')?.key).toBe('fields.errors.date')
  })

  it('datetime sai com o deslocamento do fuso local', () => {
    const wire = toWire(field('datetime'), '2026-10-20T08:30') as string
    expect(wire).toMatch(/^2026-10-20T08:30:00[+-]\d{2}:\d{2}$/)
    expect(new Date(wire).getTime()).toBe(new Date(2026, 9, 20, 8, 30).getTime())
    expect(localToOffset('2026-13-01T00:00')).toBeNull()
    expect(fromWire(field('datetime'), wire)).toBe('2026-10-20T08:30:00')
  })

  it('enum só aceita os valores declarados', () => {
    const color = field('enum', { enumValues: ['azul', 'verde'] })
    expect(validate(color, 'azul')).toBeNull()
    expect(validate(color, 'roxo')?.key).toBe('fields.errors.enum')
  })

  it('json e table são interpretados', () => {
    expect(toWire(field('json'), '{"a": [1, 2]}')).toEqual({ a: [1, 2] })
    expect(validate(field('json'), '{a:1}')?.key).toBe('fields.errors.json')
    expect(toWire(field('table'), '[{"x": 1}]')).toEqual([{ x: 1 }])
    expect(validate(field('table'), '[1, 2]')?.key).toBe('fields.errors.table')
  })

  it('table importa CSV tipando pelas colunas', () => {
    const rows = parseCsv('nome;valor;ativo\n"Ana; filha";10,5;sim\nBia;2;não\n', [
      { name: 'valor', type: 'number' },
      { name: 'ativo', type: 'bool' },
    ])
    expect(rows).toEqual([
      { nome: 'Ana; filha', valor: 10.5, ativo: true },
      { nome: 'Bia', valor: 2, ativo: false },
    ])
  })

  it('file confere tamanho e tipo, e vai como parte do multipart', () => {
    const pdf = new File(['%PDF'], 'a.pdf', { type: 'application/pdf' })
    expect(validate(field('file', { contentTypes: ['application/pdf'] }), pdf)).toBeNull()
    expect(validate(field('file', { contentTypes: ['text/html'] }), pdf)?.key).toBe('fields.errors.fileType')
    expect(validate(field('file'), pdf, 2)?.key).toBe('fields.errors.fileSize')
    const run = collectInputs([field('file', { name: 'html' }), field('string', { name: 'assunto' })], {
      html: pdf,
      assunto: 'oi',
    })
    expect(run).toEqual({ inputs: { assunto: 'oi' }, files: [['html', pdf]] })

    // A parte "inputs" vai como texto (um Blob viraria o arquivo "blob"); cada arquivo, pelo nome do campo.
    const form = toFormData(run, { version: '2', idempotencyKey: '' })
    expect(form.get('inputs')).toBe('{"assunto":"oi"}')
    expect((form.get('html') as File).name).toBe(pdf.name)
    expect(form.get('version')).toBe('2')
    expect(form.has('idempotencyKey')).toBe(false)
  })

  it('list valida e serializa cada item com o widget do tipo do item', () => {
    const numbers = field('list', { itemType: 'number' })
    expect(toWire(numbers, ['1', '2,5'])).toEqual([1, 2.5])
    expect(validate(numbers, ['1', 'x'])).toEqual({
      key: 'fields.errors.item',
      params: { index: 2, reason: 'fields.errors.number' },
    })
    expect(fromWire(numbers, [3, 4])).toEqual(['3', '4'])
  })

  it('obrigatório vazio falha; opcional e com default não', () => {
    expect(validate(field('string'), '')?.key).toBe('fields.errors.required')
    expect(validate(field('string', { required: false }), '')).toBeNull()
    expect(emptyValue(field('integer', { default: 7 }))).toBe('7')
    expect(toWire(field('string', { required: false }), '')).toBeUndefined()
  })
})
