<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { Schemas } from '@/api/client'
import FlowCanvas from './FlowCanvas.vue'
import { autoLayout, buildGraph, readLayout, type PipelineData, type StageContract, type StepRunState } from './model'

/** Fluxo ao vivo de uma execução (spec 008): o canvas da trilha, só leitura, com cor por status e tentativas no nó. */
const props = defineProps<{ run: Schemas['RunSnapshot']; definition: PipelineData; contracts: Map<string, StageContract> }>()
const { t } = useI18n()

const graph = computed(() => {
  const state = new Map<string, StepRunState>(
    props.run.steps.map((s) => [s.stepKey, { status: s.status, attempt: s.currentAttempt }] as const),
  )
  const layout = readLayout(props.definition.layout)
  const draft = buildGraph(props.definition, props.contracts, layout, state)
  const positioned = autoLayout(draft.nodes, draft.edges, layout, true)
  return buildGraph(props.definition, props.contracts, positioned, state)
})
</script>

<template>
  <FlowCanvas :nodes="graph.nodes" :edges="graph.edges" :label="t('runs.flow')" height="420px" data-testid="run-flow" />
</template>
