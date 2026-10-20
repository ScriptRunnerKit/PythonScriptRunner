import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { draftLifetimeMs, useDrafts } from './drafts'

/** Spec 008, AC-22 (D-143): rascunho local por usuário e entidade, apagado ao salvar ou depois de 7 dias. */
describe('Spec008_AC22 rascunho local', () => {
  let now = Date.UTC(2026, 9, 20)

  beforeEach(() => {
    window.localStorage.clear()
    setActivePinia(createPinia())
    useDrafts().useClock(() => now)
  })

  it('oferece restaurar a alteração não salva do mesmo usuário', () => {
    const drafts = useDrafts()
    drafts.save('login:ana', 'stage:eco', { script: 'print(1)' })

    expect(drafts.load('login:ana', 'stage:eco')?.data).toEqual({ script: 'print(1)' })
    expect(drafts.load('login:bia', 'stage:eco'), 'rascunhos de outro usuário nunca aparecem').toBeNull()
  })

  it('depois de salvar com sucesso, a oferta some', () => {
    const drafts = useDrafts()
    drafts.save('login:ana', 'stage:eco', { script: 'x' })
    drafts.remove('login:ana', 'stage:eco')

    expect(drafts.load('login:ana', 'stage:eco')).toBeNull()
  })

  it('com mais de 7 dias é descartado', () => {
    const drafts = useDrafts()
    drafts.save('login:ana', 'stage:eco', { script: 'velho' })
    drafts.save('login:ana', 'stage:novo', { script: 'novo' })
    now += draftLifetimeMs + 1

    expect(drafts.load('login:ana', 'stage:eco')).toBeNull()
    drafts.purgeExpired()
    expect(window.localStorage.length).toBe(0)
  })

  it('grava os editores abertos antes de sair (ex.: login do host)', () => {
    const drafts = useDrafts()
    let pending: unknown = { script: 'não salvo' }
    drafts.register('login:ana', 'stage:eco', () => pending)
    drafts.register('login:ana', 'pipeline:trilha', () => undefined)

    drafts.flushAll()

    expect(drafts.load('login:ana', 'stage:eco')?.data).toEqual({ script: 'não salvo' })
    expect(drafts.load('login:ana', 'pipeline:trilha')).toBeNull()
    pending = undefined
  })
})
