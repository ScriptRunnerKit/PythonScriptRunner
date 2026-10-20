<script setup lang="ts">
import { Handle, Position, type NodeProps } from '@vue-flow/core'
import { inject } from 'vue'
import { useI18n } from 'vue-i18n'
import { flowContextKey } from './context'
import { inHandle, outHandle, typeIcon, type IoNodeData } from './model'

/** Nó fixo de Entradas (à esquerda) ou Saídas (à direita) da trilha (spec 008). */
const props = defineProps<NodeProps<IoNodeData>>()
const context = inject(flowContextKey)!
const { t } = useI18n()

function disabled(name: string) {
  return context.dragging.value !== null && !context.accepts(props.id, inHandle(name))
}
</script>

<template>
  <div class="io-node" :class="`io-node--${data.kind}`" :data-node="data.kind" @click="context.nodeClicked(id)">
    <div class="io-node__header text-body-2 font-weight-medium">
      <v-icon :icon="data.kind === 'inputs' ? 'mdi-import' : 'mdi-export'" size="16" class="mr-1" aria-hidden="true" />
      {{ t(`flow.${data.kind}`) }}
    </div>
    <div v-if="data.fields.length === 0" class="text-caption text-medium-emphasis px-2 pb-2">
      {{ t(`flow.${data.kind}Empty`) }}
    </div>
    <div v-for="field in data.fields" :key="field.name" class="io-node__port" :data-field="field.name">
      <template v-if="data.kind === 'inputs'">
        <span class="io-node__label">
          <v-icon :icon="typeIcon(field.type)" size="14" aria-hidden="true" />
          {{ field.name }}
        </span>
        <Handle
          :id="outHandle(field.name)"
          type="source"
          :position="Position.Right"
          :connectable="context.editable"
          :data-handle="`out:${field.name}`"
        />
      </template>
      <template v-else>
        <Handle
          :id="inHandle(field.name)"
          type="target"
          :position="Position.Left"
          :connectable="false"
          :class="{ 'handle--disabled': disabled(field.name) }"
          :data-handle="`in:${field.name}`"
        />
        <span class="io-node__label">
          <v-icon :icon="typeIcon(field.type)" size="14" aria-hidden="true" />
          {{ field.name }}
        </span>
      </template>
    </div>
    <div v-if="data.kind === 'outputs' && context.editable && context.dragging.value" class="io-node__drop">
      <Handle id="new-output" type="target" :position="Position.Left" :connectable="true" data-handle="new-output" />
      <span class="text-caption">{{ t('flow.newOutput') }}</span>
    </div>
  </div>
</template>
