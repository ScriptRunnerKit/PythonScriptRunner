import { ref } from 'vue'
import { api, unwrap, type Schemas } from '@/api/client'
import type { FieldDefinition } from '@/components/fields/values'

const cache = ref<Schemas['FieldTypesResponse'] | null>(null)
let loading: Promise<void> | null = null

/** Tipos de campo e matriz de compatibilidade (`GET /meta/field-types`), carregados uma vez. */
export function useFieldTypes() {
  async function load() {
    if (cache.value) {
      return
    }

    loading ??= unwrap(api.GET('/meta/field-types')).then((data) => {
      cache.value = data
    })
    await loading
  }

  /**
   * Uma saída alimenta uma entrada? Mesma regra da spec 003: matriz por tipo + regras de enum, tabela, arquivo e lista
   * conferidas com a declaração.
   */
  function assignable(source: FieldDefinition, target: FieldDefinition): boolean {
    if (target.type === 'json') {
      return source.type !== 'file' && !(source.type === 'list' && source.itemType === 'file')
    }

    if (source.type === 'list' && target.type === 'list') {
      return assignable({ ...source, type: source.itemType ?? 'string' }, { ...target, type: target.itemType ?? 'string' })
    }

    if (source.type === 'enum' && target.type === 'enum') {
      return (source.enumValues ?? []).every((v) => (target.enumValues ?? []).includes(v))
    }

    if (source.type === 'file' && target.type === 'file') {
      const a = source.contentTypes ?? []
      const b = target.contentTypes ?? []
      return a.length === 0 || b.length === 0 || a.some((x) => b.some((y) => x.toLowerCase() === y.toLowerCase()))
    }

    if (source.type === 'table' && target.type === 'table') {
      const wanted = target.columns ?? []
      const given = source.columns ?? []
      return wanted.length === 0 || (given.length > 0 && wanted.every((w) => given.some((g) => g.name === w.name)))
    }

    const matrix = cache.value?.compatibility
    if (matrix) {
      return (matrix[source.type] ?? []).includes(target.type)
    }

    return source.type === target.type
  }

  return { types: cache, load, assignable }
}
