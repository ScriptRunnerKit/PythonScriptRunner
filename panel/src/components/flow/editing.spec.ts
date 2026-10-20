import { describe, expect, it } from 'vitest'
import type { FieldDefinition } from '@/components/fields/values'
import { accepts, connect, removeEdges, removeStep, renameStep } from './editing'
import {
  buildGraph,
  contractKey,
  implicitSource,
  inputsNode,
  outputsNode,
  stepNodeId,
  type PipelineData,
  type StageContract,
} from './model'

const f = (name: string, type: FieldDefinition['type'], extra: Partial<FieldDefinition> = {}): FieldDefinition => ({
  name,
  type,
  ...extra,
})

const contracts = new Map<string, StageContract>([
  [contractKey('texto', 1), { key: 'texto', version: 1, inputs: [f('n', 'integer')], outputs: [f('texto', 'string')] }],
  [
    contractKey('soma', 1),
    { key: 'soma', version: 1, inputs: [f('n', 'integer'), f('x', 'number')], outputs: [f('total', 'number')] },
  ],
])
const matrix: Record<string, string[]> = {
  integer: ['integer', 'number', 'decimal', 'json'],
  string: ['string', 'json'],
  number: ['number', 'json'],
}
const assignable = (s: FieldDefinition, t: FieldDefinition) => (matrix[s.type] ?? [s.type]).includes(t.type)

const data: PipelineData = {
  inputs: [f('n', 'integer')],
  steps: [
    { key: 'a', stageKey: 'texto', stageVersion: 1, bindings: [] },
    { key: 'b', stageKey: 'soma', stageVersion: 1, bindings: [] },
    { key: 'c', stageKey: 'soma', stageVersion: 1, bindings: [] },
  ],
  outputs: [],
}

describe('Editor visual: regras de ligação (spec 008 §3)', () => {
  it('Spec008_AC04 saída string não liga em entrada integer (handle desabilitado)', () => {
    expect(accepts(data, contracts, assignable, { nodeId: stepNodeId('a'), handleId: 'o:texto' }, stepNodeId('b'), 'i:n')).toBe(
      false,
    )
    expect(accepts(data, contracts, assignable, { nodeId: inputsNode, handleId: 'o:n' }, stepNodeId('b'), 'i:x')).toBe(true)
  })

  it('Spec008_AC05 ligação de um passo posterior para um anterior é bloqueada (linear; D-52)', () => {
    expect(accepts(data, contracts, assignable, { nodeId: stepNodeId('c'), handleId: 'o:total' }, stepNodeId('b'), 'i:x')).toBe(
      false,
    )
    expect(accepts(data, contracts, assignable, { nodeId: stepNodeId('b'), handleId: 'o:total' }, stepNodeId('c'), 'i:x')).toBe(
      true,
    )
  })

  it('ligar substitui a ligação anterior; a saída da trilha aceita saídas de passo', () => {
    let next = connect(data, { source: inputsNode, sourceHandle: 'o:n', target: stepNodeId('b'), targetHandle: 'i:x' })
    next = connect(next, { source: stepNodeId('a'), sourceHandle: 'o:texto', target: stepNodeId('b'), targetHandle: 'i:x' })
    expect(next.steps![1]!.bindings).toEqual([{ input: 'x', source: { kind: 'step', step: 'a', field: 'texto' } }])
    expect(
      accepts(next, contracts, assignable, { nodeId: stepNodeId('c'), handleId: 'o:total' }, outputsNode, 'new-output'),
    ).toBe(true)
    next = connect(next, { source: stepNodeId('c'), sourceHandle: 'o:total', target: outputsNode, targetHandle: 'new-output' })
    expect(next.outputs).toEqual([{ name: 'total', source: { step: 'c', field: 'total' } }])
  })

  it('remover passo apaga as ligações e saídas que dependiam dele; renomear atualiza referências', () => {
    let next = connect(data, { source: stepNodeId('b'), sourceHandle: 'o:total', target: stepNodeId('c'), targetHandle: 'i:x' })
    next = connect(next, { source: stepNodeId('b'), sourceHandle: 'o:total', target: outputsNode, targetHandle: 'new-output' })
    const renamed = renameStep(next, 'b', 'soma-1')
    expect(renamed.steps![2]!.bindings![0]!.source).toEqual({ kind: 'step', step: 'soma-1', field: 'total' })
    expect(renamed.outputs![0]!.source.step).toBe('soma-1')

    const removed = removeStep(next, 'b')
    expect(removed.steps!.map((s) => s.key)).toEqual(['a', 'c'])
    expect(removed.steps![1]!.bindings).toEqual([])
    expect(removed.outputs).toEqual([])
    expect(removeEdges(next, ['e:step:c:x']).steps![2]!.bindings).toEqual([])
  })

  it('ligação implícita: 1º passo ← entrada da trilha; demais ← saída de mesmo nome do anterior', () => {
    expect(implicitSource(data, 0, 'n', contracts)).toEqual({ kind: 'pipeline', field: 'n' })
    expect(implicitSource(data, 1, 'n', contracts), 'o passo "a" não tem saída "n"').toBeNull()
    const graph = buildGraph(data, contracts, { nodes: {} })
    expect(graph.edges.some((e) => e.id === `implicit:${stepNodeId('a')}:n`)).toBe(true)
  })
})
