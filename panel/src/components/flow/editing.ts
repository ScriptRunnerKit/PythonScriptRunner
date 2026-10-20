import type { Connection } from '@vue-flow/core'
import type { FieldDefinition } from '@/components/fields/values'
import {
  contractKey,
  inputsNode,
  outputsNode,
  stepNodeId,
  type BindingSource,
  type PipelineData,
  type StageContract,
} from './model'

/**
 * Regras de edição do editor visual (spec 008 §3; D-48, D-52): a definição da trilha é a fonte da verdade e cada ação
 * devolve uma nova definição.
 */
export type Assignable = (source: FieldDefinition, target: FieldDefinition) => boolean

const stepKeyPattern = /^[a-z0-9]+(-[a-z0-9]+)*$/

function clone(data: PipelineData): PipelineData {
  return {
    ...data,
    inputs: [...(data.inputs ?? [])],
    outputs: [...(data.outputs ?? [])],
    steps: (data.steps ?? []).map((s) => ({ ...s, bindings: [...(s.bindings ?? [])] })),
  }
}

export function stepIndexOf(data: PipelineData, nodeId: string): number {
  return (data.steps ?? []).findIndex((s) => stepNodeId(s.key) === nodeId)
}

function contractOf(data: PipelineData, index: number, contracts: Map<string, StageContract>) {
  const step = data.steps?.[index]
  return step ? contracts.get(contractKey(step.stageKey, step.stageVersion)) : undefined
}

/** Campo de origem de um handle de saída (entrada da trilha ou saída de um passo). */
export function sourceField(
  data: PipelineData,
  contracts: Map<string, StageContract>,
  nodeId: string,
  handleId: string,
): FieldDefinition | null {
  const name = handleId.replace(/^o:/, '')
  if (nodeId === inputsNode) {
    return (data.inputs ?? []).find((f) => f.name === name) ?? null
  }

  return contractOf(data, stepIndexOf(data, nodeId), contracts)?.outputs.find((f) => f.name === name) ?? null
}

/** Campo de destino de um handle de entrada (entrada de um passo). */
export function targetField(
  data: PipelineData,
  contracts: Map<string, StageContract>,
  nodeId: string,
  handleId: string,
): FieldDefinition | null {
  const name = handleId.replace(/^i:/, '')
  return contractOf(data, stepIndexOf(data, nodeId), contracts)?.inputs.find((f) => f.name === name) ?? null
}

/**
 * A ligação é permitida? Tipos compatíveis (matriz da spec 003) e só de passos anteriores (linear; D-52); a saída da
 * trilha aceita qualquer saída de passo.
 */
export function accepts(
  data: PipelineData,
  contracts: Map<string, StageContract>,
  assignable: Assignable,
  source: { nodeId: string; handleId: string },
  nodeId: string,
  handleId: string,
): boolean {
  const from = sourceField(data, contracts, source.nodeId, source.handleId)
  if (!from || source.nodeId === outputsNode) {
    return false
  }

  if (nodeId === outputsNode) {
    return source.nodeId !== inputsNode && handleId === 'new-output'
  }

  const targetIndex = stepIndexOf(data, nodeId)
  if (targetIndex < 0) {
    return false
  }

  if (source.nodeId !== inputsNode && stepIndexOf(data, source.nodeId) >= targetIndex) {
    return false
  }

  const to = targetField(data, contracts, nodeId, handleId)
  return to !== null && assignable(from, to)
}

function sourceOf(data: PipelineData, nodeId: string, handleId: string): BindingSource {
  const field = handleId.replace(/^o:/, '')
  return nodeId === inputsNode
    ? { kind: 'pipeline', field }
    : { kind: 'step', step: data.steps![stepIndexOf(data, nodeId)]!.key, field }
}

/** Nome livre para uma saída da trilha. */
export function uniqueOutputName(data: PipelineData, base: string): string {
  const taken = new Set((data.outputs ?? []).map((o) => o.name))
  let name = base
  for (let i = 2; taken.has(name); i++) {
    name = `${base}_${i}`
  }

  return name
}

/** Aplica uma ligação feita no canvas (substitui a ligação anterior da mesma entrada). */
export function connect(data: PipelineData, connection: Connection): PipelineData {
  const next = clone(data)
  if (!connection.sourceHandle || !connection.targetHandle) {
    return data
  }

  const source = sourceOf(next, connection.source, connection.sourceHandle)
  if (connection.target === outputsNode) {
    if (source.kind !== 'step') {
      return data
    }

    next.outputs!.push({ name: uniqueOutputName(next, source.field), source: { step: source.step, field: source.field } })
    return next
  }

  const step = next.steps![stepIndexOf(next, connection.target)]
  if (!step) {
    return data
  }

  const input = connection.targetHandle.replace(/^i:/, '')
  step.bindings = [...step.bindings!.filter((b) => b.input !== input), { input, source }]
  return next
}

/** Remove arestas: ligações de entradas (`e:<nó>:<entrada>`) ou saídas da trilha (`out:<nome>`). */
export function removeEdges(data: PipelineData, ids: string[]): PipelineData {
  const next = clone(data)
  for (const id of ids) {
    if (id.startsWith('out:')) {
      next.outputs = next.outputs!.filter((o) => `out:${o.name}` !== id)
      continue
    }

    const match = /^e:step:(.+):([^:]+)$/.exec(id)
    if (match) {
      const step = next.steps!.find((s) => s.key === match[1])
      if (step) {
        step.bindings = step.bindings!.filter((b) => b.input !== match[2])
      }
    }
  }

  return next
}

/** Remove um passo e tudo que dependia dele (ligações dos passos seguintes e saídas da trilha). */
export function removeStep(data: PipelineData, key: string): PipelineData {
  const next = clone(data)
  next.steps = next.steps!.filter((s) => s.key !== key)
  for (const step of next.steps) {
    step.bindings = step.bindings!.filter((b) => !(b.source.kind === 'step' && b.source.step === key))
  }

  next.outputs = next.outputs!.filter((o) => o.source.step !== key)
  return next
}

/** Chave livre para um passo (slug a partir da chave da Etapa). */
export function uniqueStepKey(data: PipelineData, base: string): string {
  const taken = new Set((data.steps ?? []).map((s) => s.key))
  let key = base
  for (let i = 2; taken.has(key); i++) {
    key = `${base}-${i}`
  }

  return key
}

export function addStep(data: PipelineData, stageKey: string, stageVersion: number): { data: PipelineData; key: string } {
  const next = clone(data)
  const key = uniqueStepKey(next, stageKey)
  next.steps!.push({ key, stageKey, stageVersion, bindings: [] })
  return { data: next, key }
}

/** Define (ou remove, com `undefined`) o valor fixo de uma entrada. */
export function setConstant(data: PipelineData, stepKey: string, input: string, value: unknown): PipelineData {
  const next = clone(data)
  const step = next.steps!.find((s) => s.key === stepKey)
  if (step) {
    step.bindings = step.bindings!.filter((b) => b.input !== input)
    if (value !== undefined) {
      step.bindings.push({ input, source: { kind: 'constant', value } })
    }
  }

  return next
}

/** Liga uma entrada a uma origem escolhida na lista (alternativa acessível ao canvas). */
export function bind(data: PipelineData, stepKey: string, input: string, source: BindingSource | null): PipelineData {
  const next = clone(data)
  const step = next.steps!.find((s) => s.key === stepKey)
  if (step) {
    step.bindings = step.bindings!.filter((b) => b.input !== input)
    if (source) {
      step.bindings.push({ input, source })
    }
  }

  return next
}

/** Renomeia um passo e atualiza as referências a ele. */
export function renameStep(data: PipelineData, from: string, to: string): PipelineData {
  if (!stepKeyPattern.test(to) || (data.steps ?? []).some((s) => s.key === to)) {
    return data
  }

  const next = clone(data)
  for (const step of next.steps!) {
    if (step.key === from) {
      step.key = to
    }

    step.bindings = step.bindings!.map((b) =>
      b.source.kind === 'step' && b.source.step === from ? { ...b, source: { ...b.source, step: to } } : b,
    )
  }

  next.outputs = next.outputs!.map((o) => (o.source.step === from ? { ...o, source: { ...o.source, step: to } } : o))
  return next
}

/** Muda a posição de um passo na ordem linear (a validação aponta ligações que ficaram para trás). */
export function moveStep(data: PipelineData, from: number, to: number): PipelineData {
  const next = clone(data)
  const [step] = next.steps!.splice(from, 1)
  if (step) {
    next.steps!.splice(Math.max(0, Math.min(to, next.steps!.length)), 0, step)
  }

  return next
}

export function isValidStepKey(key: string): boolean {
  return stepKeyPattern.test(key) && key.length <= 64
}
