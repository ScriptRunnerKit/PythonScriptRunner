<script setup lang="ts">
import '@vue-flow/core/dist/style.css'
import '@vue-flow/core/dist/theme-default.css'
import '@vue-flow/controls/dist/style.css'
import '@vue-flow/minimap/dist/style.css'
import './flow.css'
import { Background } from '@vue-flow/background'
import { ControlButton, Controls } from '@vue-flow/controls'
import { MiniMap } from '@vue-flow/minimap'
import {
  VueFlow,
  useVueFlow,
  type Connection,
  type Edge,
  type EdgeChange,
  type Node,
  type NodeChange,
  type NodeDragEvent,
  type OnConnectStartParams,
} from '@vue-flow/core'
import { nextTick, provide, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { flowContextKey, type DragSource, type FlowContext } from './context'
import IoNode from './IoNode.vue'
import StepNode from './StepNode.vue'

/** Canvas Vue Flow do editor de trilhas e do fluxo ao vivo (spec 008; ADR-0020). */
const props = withDefaults(
  defineProps<{
    nodes: Node[]
    edges: Edge[]
    editable?: boolean
    accepts?: (source: DragSource, nodeId: string, handleId: string) => boolean
    height?: string
    label: string
  }>(),
  { editable: false, accepts: () => () => true, height: '520px' },
)
const emit = defineEmits<{
  connect: [connection: Connection]
  removeEdges: [ids: string[]]
  removeNodes: [ids: string[]]
  moved: [positions: Record<string, { x: number; y: number }>]
  drop: [payload: string, position: { x: number; y: number }]
  inputClicked: [nodeId: string, input: string]
  nodeClicked: [nodeId: string]
}>()

const { t } = useI18n()
const id = `flow-${Math.random().toString(36).slice(2)}`
const { fitView, screenToFlowCoordinate, zoomIn, zoomOut } = useVueFlow(id)
const dragging = ref<DragSource | null>(null)

const context: FlowContext = {
  editable: props.editable,
  dragging,
  accepts: (nodeId, handleId) => (dragging.value ? props.accepts(dragging.value, nodeId, handleId) : true),
  inputClicked: (nodeId, input) => emit('inputClicked', nodeId, input),
  nodeClicked: (nodeId) => emit('nodeClicked', nodeId),
}
provide(flowContextKey, context)

function connectStart(params: { event?: MouseEvent | TouchEvent } & OnConnectStartParams) {
  if (params.nodeId && params.handleId && params.handleType === 'source') {
    dragging.value = { nodeId: params.nodeId, handleId: params.handleId }
  }
}

function isValidConnection(connection: Connection): boolean {
  return (
    props.editable &&
    Boolean(connection.sourceHandle && connection.targetHandle) &&
    props.accepts({ nodeId: connection.source, handleId: connection.sourceHandle! }, connection.target, connection.targetHandle!)
  )
}

function edgesChanged(changes: EdgeChange[]) {
  const removed = changes.filter((c) => c.type === 'remove').map((c) => c.id)
  if (removed.length) {
    emit('removeEdges', removed)
  }
}

function nodesChanged(changes: NodeChange[]) {
  const removed = changes.filter((c) => c.type === 'remove').map((c) => c.id)
  if (removed.length) {
    emit('removeNodes', removed)
  }
}

function dragStop(event: NodeDragEvent) {
  emit('moved', Object.fromEntries(event.nodes.map((n) => [n.id, { x: Math.round(n.position.x), y: Math.round(n.position.y) }])))
}

function onDragOver(event: DragEvent) {
  if (props.editable && event.dataTransfer?.types.includes('application/x-psr-stage')) {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'copy'
  }
}

function onDrop(event: DragEvent) {
  const payload = event.dataTransfer?.getData('application/x-psr-stage')
  if (props.editable && payload) {
    event.preventDefault()
    emit('drop', payload, screenToFlowCoordinate({ x: event.clientX, y: event.clientY }))
  }
}

/** Centraliza um nó (problemas da validação). */
function focus(nodeId: string) {
  void fitView({ nodes: [nodeId], duration: 300, maxZoom: 1.2, padding: 0.4 })
}

watch(
  () => props.nodes.length,
  () => nextTick(() => fitView({ padding: 0.15 })),
)

defineExpose({ focus, fit: () => fitView({ padding: 0.15 }) })
</script>

<template>
  <div class="flow-canvas" :style="{ height }" role="application" :aria-label="label" @dragover="onDragOver" @drop="onDrop">
    <VueFlow
      :id="id"
      :nodes="nodes"
      :edges="edges"
      :nodes-draggable="editable"
      :nodes-connectable="editable"
      :elements-selectable="editable"
      :edges-updatable="false"
      :delete-key-code="editable ? ['Delete', 'Backspace'] : null"
      :is-valid-connection="isValidConnection"
      :min-zoom="0.2"
      :max-zoom="2"
      fit-view-on-init
      @connect="(c: Connection) => emit('connect', c)"
      @connect-start="connectStart"
      @connect-end="dragging = null"
      @edges-change="edgesChanged"
      @nodes-change="nodesChanged"
      @node-drag-stop="dragStop"
    >
      <template #node-step="nodeProps"><StepNode v-bind="nodeProps" /></template>
      <template #node-io="nodeProps"><IoNode v-bind="nodeProps" /></template>
      <Background :gap="18" />
      <MiniMap pannable zoomable :aria-label="t('flow.minimap')" />
      <!-- Botões próprios: os padrões do Vue Flow não têm nome acessível (spec 008, AC-15). -->
      <Controls :show-zoom="false" :show-fit-view="false" :show-interactive="false">
        <ControlButton :title="t('flow.zoomIn')" :aria-label="t('flow.zoomIn')" @click="zoomIn()">
          <v-icon icon="mdi-plus" size="16" aria-hidden="true" />
        </ControlButton>
        <ControlButton :title="t('flow.zoomOut')" :aria-label="t('flow.zoomOut')" @click="zoomOut()">
          <v-icon icon="mdi-minus" size="16" aria-hidden="true" />
        </ControlButton>
        <ControlButton :title="t('flow.fitView')" :aria-label="t('flow.fitView')" @click="fitView({ padding: 0.15 })">
          <v-icon icon="mdi-fit-to-screen-outline" size="16" aria-hidden="true" />
        </ControlButton>
      </Controls>
    </VueFlow>
  </div>
</template>
