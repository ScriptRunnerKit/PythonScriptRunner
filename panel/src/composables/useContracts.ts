import { reactive } from 'vue'
import { api, unwrap } from '@/api/client'
import { contractKey, type StageContract } from '@/components/flow/model'

const cache = reactive(new Map<string, StageContract | null>())
const pending = new Map<string, Promise<StageContract | null>>()

/** Contratos (entradas e saídas) das versões de Etapa usadas pelos passos, com cache por `chave@versão`. */
export function useContracts() {
  async function load(stageKey: string, version: number): Promise<StageContract | null> {
    const key = contractKey(stageKey, version)
    if (cache.has(key)) {
      return cache.get(key) ?? null
    }

    let request = pending.get(key)
    if (!request) {
      request = unwrap(api.GET('/stages/{key}/versions/{version}', { params: { path: { key: stageKey, version } } }))
        .then((detail) => ({
          key: stageKey,
          version,
          inputs: detail.data.inputs ?? [],
          outputs: detail.data.outputs ?? [],
        }))
        .catch(() => null)
        .then((contract) => {
          cache.set(key, contract)
          pending.delete(key)
          return contract
        })
      pending.set(key, request)
    }

    return request
  }

  async function loadAll(steps: { stageKey: string; stageVersion: number }[]): Promise<Map<string, StageContract>> {
    await Promise.all(steps.map((s) => load(s.stageKey, s.stageVersion)))
    const result = new Map<string, StageContract>()
    for (const step of steps) {
      const contract = cache.get(contractKey(step.stageKey, step.stageVersion))
      if (contract) {
        result.set(contractKey(step.stageKey, step.stageVersion), contract)
      }
    }

    return result
  }

  return { load, loadAll }
}
