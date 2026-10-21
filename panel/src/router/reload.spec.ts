import { describe, expect, it } from 'vitest'
import { claimReload, isLoadError } from './reload'

function memory(): Pick<Storage, 'getItem' | 'setItem'> {
  const items = new Map<string, string>()
  return { getItem: (key) => items.get(key) ?? null, setItem: (key, value) => void items.set(key, value) }
}

describe('Recarga de tela que não carregou', () => {
  it('Spec008_AC27 reconhece a falha de baixar um pedaço do painel nos três navegadores', () => {
    expect(isLoadError(new TypeError('Failed to fetch dynamically imported module: http://x/psr/assets/RunListView-a1.js'))).toBe(
      true,
    )
    expect(isLoadError(new TypeError('error loading dynamically imported module'))).toBe(true)
    expect(isLoadError(new TypeError('Importing a module script failed.'))).toBe(true)
    expect(isLoadError(new Error('Unable to preload CSS for /psr/assets/RunListView-a1.css'))).toBe(true)
    expect(isLoadError(new Error('Request failed with status 500'))).toBe(false)
  })

  it('Spec008_AC27 recarrega uma vez por rota e não entra em laço dentro de 10 s', () => {
    const storage = memory()

    expect(claimReload('/execucoes', 1_000, storage)).toBe(true)
    expect(claimReload('/execucoes', 5_000, storage)).toBe(false)
    expect(claimReload('/agendamentos', 6_000, storage)).toBe(true)
    expect(claimReload('/agendamentos', 16_001, storage)).toBe(true)
  })

  it('Spec008_AC27 sem onde guardar a marca, não recarrega (melhor o aviso do que um laço)', () => {
    const blocked: Pick<Storage, 'getItem' | 'setItem'> = {
      getItem: () => null,
      setItem: () => {
        throw new DOMException('quota', 'QuotaExceededError')
      },
    }

    expect(claimReload('/execucoes', 1_000, null)).toBe(false)
    expect(claimReload('/execucoes', 1_000, blocked)).toBe(false)
  })
})
