import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import ptBR from './pt-BR'

/** Spec 008 (i18n): toda chave usada no código existe no catálogo pt-BR. */
function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    return statSync(path).isDirectory() ? files(path) : /\.(vue|ts)$/.test(name) && !name.endsWith('.spec.ts') ? [path] : []
  })
}

function has(key: string): boolean {
  let node: unknown = ptBR
  for (const part of key.split('.')) {
    if (node === null || typeof node !== 'object' || !(part in node)) {
      return false
    }

    node = (node as Record<string, unknown>)[part]
  }

  return typeof node === 'string'
}

describe('Catálogo pt-BR', () => {
  it('tem todas as chaves usadas no código', () => {
    const source = files(join(__dirname, '..'))
      .map((file) => readFileSync(file, 'utf8'))
      .join('\n')
    const keys = new Set(
      [
        ...source.matchAll(/\bte?\(\s*'([a-zA-Z]+(?:\.[a-zA-Z]+)+)'/g),
        ...source.matchAll(/\b(?:key|title|label): '([a-z][a-zA-Z]+\.[a-zA-Z.]+)'/g),
      ]
        .map((m) => m[1]!)
        .filter((key) => !key.startsWith('ctx.')),
    )

    expect([...keys].filter((key) => !has(key))).toEqual([])
    expect(keys.size).toBeGreaterThan(300)
  })

  it('tem os textos das chaves dinâmicas (status, tipos, origens, papéis)', () => {
    const dynamic = [
      ...['Pending', 'Queued', 'Running', 'Succeeded', 'Failed', 'Cancelling', 'Cancelled'].map((s) => `status.run.${s}`),
      ...['Pending', 'Queued', 'Running', 'Succeeded', 'Failed', 'TimedOut', 'Cancelled', 'Skipped'].map(
        (s) => `status.step.${s}`,
      ),
      ...['Draft', 'Published', 'Archived'].map((s) => `status.version.${s}`),
      ...['Pipeline', 'PipelineTest', 'Stage', 'StageTest'].map((k) => `kind.${k}`),
      ...['Library', 'Dashboard', 'Schedule', 'QueueCommand'].map((k) => `trigger.${k}`),
      ...['Reader', 'Executor', 'Author', 'Admin'].map((r) => `roles.${r}`),
      ...['FixedLogin', 'Filter', 'Host', 'Localhost'].map((m) => `accessMode.${m}`),
      ...['stages', 'pipelines'].flatMap((k) => [`catalog.${k}.title`, `catalog.${k}.subtitle`, `catalog.${k}.create`]),
    ]
    expect(dynamic.filter((key) => !has(key))).toEqual([])
  })
})
