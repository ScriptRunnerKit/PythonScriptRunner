import dagre from '@dagrejs/dagre'
import type { Edge, Node, XYPosition } from '@vue-flow/core'
import type { Schemas } from '@/api/client'
import type { FieldDefinition } from '@/components/fields/values'

export type PipelineData = Schemas['PipelineDraftData']
export type PipelineStep = Schemas['PipelineStepDefinition']
export type BindingSource = Schemas['BindingSource']

/** Contrato de uma versão de Etapa usada por um passo. */
export interface StageContract {
  key: string
  version: number
  name?: string
  inputs: FieldDefinition[]
  outputs: FieldDefinition[]
}

/** Estado de um passo numa execução (fluxo ao vivo). */
export interface StepRunState {
  status: string
  attempt: number
}

export type InputState = 'edge' | 'constant' | 'implicit' | 'none'

export interface StepNodeData {
  kind: 'step'
  index: number
  step: PipelineStep
  contract: StageContract | null
  inputs: { field: FieldDefinition; state: InputState; implicitFrom: string | null; constant: unknown }[]
  run?: StepRunState
}

export interface IoNodeData {
  kind: 'inputs' | 'outputs'
  fields: FieldDefinition[]
}

export const inputsNode = 'inputs'
export const outputsNode = 'outputs'

export const stepNodeId = (key: string) => `step:${key}`
export const inHandle = (name: string) => `i:${name}`
export const outHandle = (name: string) => `o:${name}`

/** Chave de uma versão de Etapa (`chave@versão`). */
export const contractKey = (stageKey: string, version: number) => `${stageKey}@${version}`

/** Posições salvas no `layout` do rascunho (não afetam a execução). */
export interface Layout {
  nodes: Record<string, XYPosition>
}

export function readLayout(value: unknown): Layout {
  const nodes = (value as { nodes?: Record<string, XYPosition> } | null)?.nodes
  return { nodes: nodes && typeof nodes === 'object' ? { ...nodes } : {} }
}

/** Origem implícita de uma entrada sem ligação (spec 003): 1º passo ← entrada da trilha; demais ← saída do passo anterior. */
export function implicitSource(
  data: PipelineData,
  index: number,
  input: string,
  contracts: Map<string, StageContract>,
): BindingSource | null {
  if (index === 0) {
    return (data.inputs ?? []).some((f) => f.name === input) ? { kind: 'pipeline', field: input } : null
  }

  const previous = data.steps?.[index - 1]
  const contract = previous ? contracts.get(contractKey(previous.stageKey, previous.stageVersion)) : undefined
  return previous && contract?.outputs.some((o) => o.name === input) ? { kind: 'step', step: previous.key, field: input } : null
}

function describe(source: BindingSource | null): string | null {
  if (!source) {
    return null
  }

  return source.kind === 'pipeline'
    ? `$pipeline.${source.field}`
    : source.kind === 'step'
      ? `$steps.${source.step}.${source.field}`
      : null
}

/** Tipo de uma saída da trilha (herdado do passo de origem). */
export function outputField(
  data: PipelineData,
  output: Schemas['PipelineOutputDefinition'],
  contracts: Map<string, StageContract>,
): FieldDefinition {
  const step = data.steps?.find((s) => s.key === output.source.step)
  const contract = step ? contracts.get(contractKey(step.stageKey, step.stageVersion)) : undefined
  const field = contract?.outputs.find((o) => o.name === output.source.field)
  return { ...(field ?? { type: 'json' }), name: output.name, description: output.description ?? field?.description }
}

/** Monta nós e arestas do canvas a partir da definição da trilha (fonte da verdade do editor). */
export function buildGraph(
  data: PipelineData,
  contracts: Map<string, StageContract>,
  layout: Layout,
  runState?: Map<string, StepRunState>,
): { nodes: Node[]; edges: Edge[] } {
  const steps = data.steps ?? []
  const nodes: Node[] = [
    {
      id: inputsNode,
      type: 'io',
      position: layout.nodes[inputsNode] ?? { x: 0, y: 0 },
      data: { kind: 'inputs', fields: data.inputs ?? [] } satisfies IoNodeData,
      deletable: false,
    },
  ]
  const edges: Edge[] = []
  steps.forEach((step, index) => {
    const contract = contracts.get(contractKey(step.stageKey, step.stageVersion)) ?? null
    const id = stepNodeId(step.key)
    const inputs = (contract?.inputs ?? []).map((field) => {
      const binding = (step.bindings ?? []).find((b) => b.input === field.name)
      const implicit = binding ? null : describe(implicitSource(data, index, field.name, contracts))
      const state: InputState = binding
        ? binding.source.kind === 'constant'
          ? 'constant'
          : 'edge'
        : implicit
          ? 'implicit'
          : 'none'
      return {
        field,
        state,
        implicitFrom: implicit,
        constant: binding?.source.kind === 'constant' ? binding.source.value : undefined,
      }
    })
    const run = runState?.get(step.key)
    nodes.push({
      id,
      type: 'step',
      position: layout.nodes[id] ?? { x: 0, y: 0 },
      data: { kind: 'step', index, step, contract, inputs, run } satisfies StepNodeData,
      class: run ? `step--${run.status.toLowerCase()}` : undefined,
    })
    for (const binding of step.bindings ?? []) {
      const source = binding.source
      if (source.kind === 'constant') {
        continue
      }

      const fromNode = source.kind === 'pipeline' ? inputsNode : stepNodeId(source.step)
      const running = run?.status === 'Running'
      edges.push({
        id: `e:${id}:${binding.input}`,
        source: fromNode,
        sourceHandle: outHandle(source.field),
        target: id,
        targetHandle: inHandle(binding.input),
        animated: running,
      })
    }

    // Ligações implícitas aparecem tracejadas (não são salvas como ligação).
    for (const input of inputs.filter((x) => x.state === 'implicit')) {
      const source = implicitSource(data, index, input.field.name, contracts)
      if (!source || source.kind === 'constant') {
        continue
      }

      edges.push({
        id: `implicit:${id}:${input.field.name}`,
        source: source.kind === 'pipeline' ? inputsNode : stepNodeId(source.step),
        sourceHandle: outHandle(source.field),
        target: id,
        targetHandle: inHandle(input.field.name),
        class: 'edge--implicit',
        selectable: false,
        deletable: false,
        animated: run?.status === 'Running',
      })
    }
  })
  const outputs = (data.outputs ?? []).map((o) => outputField(data, o, contracts))
  nodes.push({
    id: outputsNode,
    type: 'io',
    position: layout.nodes[outputsNode] ?? { x: 0, y: 0 },
    data: { kind: 'outputs', fields: outputs } satisfies IoNodeData,
    deletable: false,
  })
  for (const output of data.outputs ?? []) {
    edges.push({
      id: `out:${output.name}`,
      source: stepNodeId(output.source.step),
      sourceHandle: outHandle(output.source.field),
      target: outputsNode,
      targetHandle: inHandle(output.name),
    })
  }

  return { nodes, edges }
}

/** Auto-layout da esquerda para a direita (dagre); só os nós sem posição salva quando `onlyMissing`. */
export function autoLayout(nodes: Node[], edges: Edge[], layout: Layout, onlyMissing = false): Layout {
  const graph = new dagre.graphlib.Graph()
  graph.setGraph({ rankdir: 'LR', nodesep: 40, ranksep: 90 })
  graph.setDefaultEdgeLabel(() => ({}))
  for (const node of nodes) {
    const rows =
      node.type === 'step'
        ? Math.max((node.data as StepNodeData).inputs.length, (node.data as StepNodeData).contract?.outputs.length ?? 0)
        : (node.data as IoNodeData).fields.length
    graph.setNode(node.id, { width: 240, height: 70 + rows * 26 })
  }

  // A ordem dos passos define as colunas (linear; D-52).
  const ordered = nodes
    .filter((n) => n.type === 'step')
    .sort((a, b) => (a.data as StepNodeData).index - (b.data as StepNodeData).index)
  const chain = [inputsNode, ...ordered.map((n) => n.id), outputsNode]
  chain.slice(1).forEach((id, i) => graph.setEdge(chain[i]!, id))
  edges.filter((e) => !e.id.startsWith('implicit:')).forEach((e) => graph.setEdge(e.source, e.target))
  dagre.layout(graph)
  const result: Layout = { nodes: { ...layout.nodes } }
  for (const node of nodes) {
    if (onlyMissing && layout.nodes[node.id]) {
      continue
    }

    const { x, y, width, height } = graph.node(node.id)
    result.nodes[node.id] = { x: Math.round(x - width / 2), y: Math.round(y - height / 2) }
  }

  return result
}

/** Ícone de cada tipo de campo (handles e listas). */
export function typeIcon(type: string | undefined): string {
  switch (type) {
    case 'number':
    case 'integer':
      return 'mdi-numeric'
    case 'decimal':
      return 'mdi-decimal'
    case 'string':
      return 'mdi-format-text'
    case 'bool':
      return 'mdi-toggle-switch-outline'
    case 'date':
      return 'mdi-calendar'
    case 'datetime':
      return 'mdi-calendar-clock'
    case 'enum':
      return 'mdi-format-list-bulleted'
    case 'json':
      return 'mdi-code-json'
    case 'table':
      return 'mdi-table'
    case 'file':
      return 'mdi-file-outline'
    case 'list':
      return 'mdi-format-list-numbered'
    default:
      return 'mdi-help-circle-outline'
  }
}

export function typeLabel(field: FieldDefinition): string {
  return field.type === 'list' ? `list<${field.itemType ?? '?'}>` : field.type
}
