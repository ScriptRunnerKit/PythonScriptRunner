<script setup lang="ts">
import { Handle, Position, type NodeProps } from '@vue-flow/core'
import { computed, inject } from 'vue'
import { useI18n } from 'vue-i18n'
import StatusChip from '@/components/common/StatusChip.vue'
import { flowContextKey } from './context'
import { inHandle, outHandle, typeIcon, typeLabel, type StepNodeData } from './model'

/** Nó de um passo: entradas à esquerda, saídas à direita, ícone do tipo; no fluxo ao vivo, status e tentativas. */
const props = defineProps<NodeProps<StepNodeData>>()
const context = inject(flowContextKey)!
const { t } = useI18n()

const outputs = computed(() => props.data.contract?.outputs ?? [])
const running = computed(() => props.data.run?.status === 'Running')

function disabled(input: string) {
  const dragging = context.dragging.value
  return dragging !== null && !context.accepts(props.id, inHandle(input))
}
</script>

<template>
  <div
    class="step-node"
    :class="{ 'step-node--running': running }"
    :data-step="data.step.key"
    :data-status="data.run?.status"
    @click="context.nodeClicked(id)"
  >
    <div class="step-node__header">
      <span class="step-node__order" :aria-label="t('flow.order', { order: data.index + 1 })">{{ data.index + 1 }}</span>
      <div class="step-node__title">
        <div class="text-body-2 font-weight-medium">{{ data.step.displayName || data.step.key }}</div>
        <div class="text-caption text-medium-emphasis">{{ data.step.stageKey }}@{{ data.step.stageVersion }}</div>
      </div>
      <v-badge
        v-if="data.run && data.run.attempt > 1"
        :content="`#${data.run.attempt}`"
        color="warning"
        inline
        :aria-label="t('flow.attempt', { attempt: data.run.attempt })"
      />
    </div>
    <div v-if="data.run" class="px-2 pb-1"><StatusChip :status="data.run.status" kind="step" /></div>
    <div v-if="!data.contract" class="text-caption text-error px-2 pb-2">{{ t('flow.missingStage') }}</div>
    <div class="step-node__ports">
      <div class="step-node__inputs">
        <div
          v-for="input in data.inputs"
          :key="input.field.name"
          class="step-node__port step-node__port--in"
          :class="`step-node__port--${input.state}`"
          :data-input="input.field.name"
        >
          <Handle
            :id="inHandle(input.field.name)"
            type="target"
            :position="Position.Left"
            :connectable="context.editable && !disabled(input.field.name)"
            :class="{ 'handle--disabled': disabled(input.field.name) }"
            :aria-disabled="disabled(input.field.name)"
            :data-handle="`in:${input.field.name}`"
          />
          <button
            type="button"
            class="step-node__port-label"
            :title="
              input.implicitFrom
                ? t('flow.implicit', { source: input.implicitFrom })
                : input.state === 'constant'
                  ? t('flow.constant')
                  : typeLabel(input.field)
            "
            @click.stop="context.inputClicked(id, input.field.name)"
          >
            <v-icon :icon="typeIcon(input.field.type)" size="14" aria-hidden="true" />
            {{ input.field.name
            }}<span v-if="input.field.required !== false && input.state === 'none'" class="text-error">*</span>
            <v-icon v-if="input.state === 'constant'" icon="mdi-pin" size="12" aria-hidden="true" />
          </button>
        </div>
      </div>
      <div class="step-node__outputs">
        <div v-for="output in outputs" :key="output.name" class="step-node__port step-node__port--out" :data-output="output.name">
          <span class="step-node__port-label">
            {{ output.name }}
            <v-icon :icon="typeIcon(output.type)" size="14" aria-hidden="true" />
          </span>
          <Handle
            :id="outHandle(output.name)"
            type="source"
            :position="Position.Right"
            :connectable="context.editable"
            :data-handle="`out:${output.name}`"
          />
        </div>
      </div>
    </div>
  </div>
</template>
