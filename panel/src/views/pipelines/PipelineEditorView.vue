<script setup lang="ts">
import type { Connection } from '@vue-flow/core'
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ApiError, api, apiFetch, ensureOk, unwrap, unwrapWithETag, type Schemas } from '@/api/client'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import ConflictDialog from '@/components/common/ConflictDialog.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import ProblemList from '@/components/common/ProblemList.vue'
import FieldBuilder from '@/components/fields/FieldBuilder.vue'
import FieldForm from '@/components/fields/FieldForm.vue'
import FieldInput from '@/components/fields/FieldInput.vue'
import { collectInputs, fromWire, toFormData, toWire, type FieldDefinition, type UiValue } from '@/components/fields/values'
import * as editing from '@/components/flow/editing'
import {
  autoLayout,
  buildGraph,
  contractKey,
  implicitSource,
  inputsNode,
  outputsNode,
  readLayout,
  stepNodeId,
  typeLabel,
  type BindingSource,
  type Layout,
  type PipelineData,
  type StageContract,
} from '@/components/flow/model'
import { useErrorText } from '@/composables/useApiAction'
import { useContracts } from '@/composables/useContracts'
import { useFieldTypes } from '@/composables/useFieldTypes'
import { useLocalDraft } from '@/composables/useLocalDraft'
import { useNotify } from '@/stores/notify'
import { formatDateTime } from '@/utils/format'

const FlowCanvas = defineAsyncComponent(() => import('@/components/flow/FlowCanvas.vue'))
type Draft = Schemas['PipelineDraft']

/**
 * Editor visual de trilhas (spec 008 §3; D-48): paleta de Etapas publicadas, canvas com *handles* tipados (incompatíveis
 * desabilitados; ligações só para passos posteriores — D-52), valores fixos, ordem linear, auto-layout, validação contínua,
 * testar sem publicar (D-97) e publicar com avisos; lista editável como alternativa acessível ao canvas.
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const notify = useNotify()
const errorText = useErrorText()
const contractStore = useContracts()
const fieldTypes = useFieldTypes()
const key = computed(() => String(route.params.key))

const draft = ref<Draft | null>(null)
const etag = ref('')
const form = ref<PipelineData | null>(null)
const contracts = shallowRef(new Map<string, StageContract>())
const layout = ref<Layout>({ nodes: {} })
const view = ref<'canvas' | 'list'>('canvas')
const selection = ref<string | null>(null)
const report = ref<Schemas['ValidationReport'] | null>(null)
const validating = ref(false)
const saving = ref(false)
const publishing = ref(false)
const conflict = ref<Draft | null>(null)
const publication = ref<Schemas['PipelinePublication'] | null>(null)
const publishError = ref<string | null>(null)
const confirmDiscard = ref(false)
const testOpen = ref(false)
const testValues = ref<Record<string, UiValue>>({})
const testForm = ref<InstanceType<typeof FieldForm> | null>(null)
const testing = ref(false)
const constantEdit = ref<{ step: string; field: FieldDefinition } | null>(null)
const constantValue = shallowRef<UiValue>(null)
const canvas = ref<{ focus: (id: string) => void; fit: () => void } | null>(null)
const palette = ref<Schemas['StageSummary'][]>([])
const paletteSearch = ref('')
const paletteVersions = ref<Record<string, number[]>>({})
const paletteChoice = ref<Record<string, number>>({})
const missing = ref(false)

// Base da comparação montada como o formulário (um rascunho sem layout salvo não conta como alterado).
const baseline = ref('')
const dirty = computed(
  () => form.value !== null && draft.value !== null && JSON.stringify(withLayout(form.value)) !== baseline.value,
)
const local = useLocalDraft<PipelineData>(
  () => `pipeline:${key.value}`,
  () => (dirty.value ? withLayout(form.value!) : undefined),
)

function withLayout(data: PipelineData): PipelineData {
  return { ...data, layout: { nodes: layout.value.nodes } }
}

function take(result: { data: Draft; etag: string }) {
  draft.value = result.data
  etag.value = result.etag || `"${result.data.rowVersion}"`
  form.value = structuredClone({ ...result.data.data, layout: undefined })
  layout.value = readLayout(result.data.data.layout)
  baseline.value = JSON.stringify(withLayout(form.value))
}

async function loadContracts() {
  if (form.value) {
    contracts.value = await contractStore.loadAll(form.value.steps ?? [])
  }
}

async function load() {
  try {
    take(await unwrapWithETag(api.GET('/pipelines/{key}/draft', { params: { path: { key: key.value } } })))
    missing.value = false
    local.check()
    await loadContracts()
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      missing.value = true
    } else {
      notify.error(errorText(error))
    }
  }
}

async function loadPalette() {
  const page = await unwrap(
    api.GET('/stages', {
      params: { query: { status: 'Published', search: paletteSearch.value || undefined, pageSize: 100 } },
    }),
  ).catch(() => null)
  palette.value = (page?.items ?? []).filter((s) => s.latestPublishedVersion)
}

async function paletteVersionsOf(stage: Schemas['StageSummary']) {
  if (paletteVersions.value[stage.key]) {
    return
  }

  const detail = await unwrap(api.GET('/stages/{key}', { params: { path: { key: stage.key } } })).catch(() => null)
  paletteVersions.value = {
    ...paletteVersions.value,
    [stage.key]: (detail?.versions ?? []).filter((v) => v.status === 'Published').map((v) => v.version),
  }
}

let paletteTimer: ReturnType<typeof setTimeout> | null = null
watch(paletteSearch, () => {
  if (paletteTimer) {
    clearTimeout(paletteTimer)
  }

  paletteTimer = setTimeout(loadPalette, 300)
})

onMounted(async () => {
  await Promise.all([load(), loadPalette(), fieldTypes.load()])
  window.addEventListener('keydown', onKey)
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))

function onKey(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
    event.preventDefault()
    void save()
  }
}

const graph = computed(() => {
  if (!form.value) {
    return { nodes: [], edges: [] }
  }

  const first = buildGraph(form.value, contracts.value, layout.value)
  const placed = autoLayout(first.nodes, first.edges, layout.value, true)
  return buildGraph(form.value, contracts.value, placed)
})

function update(next: PipelineData) {
  form.value = next
}

function accepts(source: { nodeId: string; handleId: string }, nodeId: string, handleId: string) {
  return form.value !== null && editing.accepts(form.value, contracts.value, fieldTypes.assignable, source, nodeId, handleId)
}

function onConnect(connection: Connection) {
  if (
    form.value &&
    connection.sourceHandle &&
    connection.targetHandle &&
    accepts({ nodeId: connection.source, handleId: connection.sourceHandle }, connection.target, connection.targetHandle)
  ) {
    update(editing.connect(form.value, connection))
  }
}

function onRemoveEdges(ids: string[]) {
  if (form.value) {
    update(editing.removeEdges(form.value, ids))
  }
}

function onRemoveNodes(ids: string[]) {
  let next = form.value
  for (const id of ids) {
    if (next && id.startsWith('step:')) {
      next = editing.removeStep(next, id.slice(5))
    }
  }

  if (next) {
    update(next)
  }
}

function onMoved(positions: Record<string, { x: number; y: number }>) {
  layout.value = { nodes: { ...layout.value.nodes, ...positions } }
}

async function onDrop(payload: string, position: { x: number; y: number }) {
  const [stageKey, version] = payload.split('@')
  if (!form.value || !stageKey || !version) {
    return
  }

  const added = editing.addStep(form.value, stageKey, Number(version))
  layout.value = { nodes: { ...layout.value.nodes, [stepNodeId(added.key)]: position } }
  update(added.data)
  await loadContracts()
}

function addFromPalette(stage: Schemas['StageSummary']) {
  const version = paletteChoice.value[stage.key] ?? stage.latestPublishedVersion!
  void onDrop(`${stage.key}@${version}`, { x: 0, y: 0 }).then(() => {
    const key = form.value?.steps?.at(-1)?.key
    if (key) {
      const { [stepNodeId(key)]: _, ...rest } = layout.value.nodes
      layout.value = { nodes: rest }
    }
  })
}

function dragStage(event: DragEvent, stage: Schemas['StageSummary']) {
  event.dataTransfer?.setData(
    'application/x-psr-stage',
    `${stage.key}@${paletteChoice.value[stage.key] ?? stage.latestPublishedVersion}`,
  )
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'copy'
  }
}

function autoArrange() {
  layout.value = autoLayout(graph.value.nodes, graph.value.edges, { nodes: {} })
  canvas.value?.fit()
}

// Ordem linear (barra reordenável).
const dragIndex = ref<number | null>(null)
function dropOrder(index: number) {
  if (form.value && dragIndex.value !== null && dragIndex.value !== index) {
    update(editing.moveStep(form.value, dragIndex.value, index))
  }

  dragIndex.value = null
}

// Clique numa entrada: valor fixo ou ver a ligação implícita.
function onInputClicked(nodeId: string, input: string) {
  if (!form.value) {
    return
  }

  const index = editing.stepIndexOf(form.value, nodeId)
  const step = form.value.steps?.[index]
  const field = step && contracts.value.get(contractKey(step.stageKey, step.stageVersion))?.inputs.find((f) => f.name === input)
  if (!step || !field) {
    return
  }

  const binding = step.bindings?.find((b) => b.input === input)
  const value =
    binding?.source.kind === 'constant'
      ? fromWire({ ...field, required: false }, binding.source.value)
      : fromWire({ ...field, required: false, default: undefined }, field.default ?? null)
  constantEdit.value = { step: step.key, field }
  constantValue.value = value
}

const constantInfo = computed(() => {
  if (!constantEdit.value || !form.value) {
    return null
  }

  const index = form.value.steps?.findIndex((s) => s.key === constantEdit.value!.step) ?? -1
  const step = form.value.steps?.[index]
  const binding = step?.bindings?.find((b) => b.input === constantEdit.value!.field.name)
  const implicit = binding ? null : implicitSource(form.value, index, constantEdit.value.field.name, contracts.value)
  return { binding, implicit }
})

function describe(source: BindingSource | null | undefined) {
  if (!source) {
    return t('pipelines.editor.unbound')
  }

  return source.kind === 'pipeline'
    ? `$pipeline.${source.field}`
    : source.kind === 'step'
      ? `$steps.${source.step}.${source.field}`
      : t('flow.constant')
}

function applyConstant(remove = false) {
  if (!form.value || !constantEdit.value) {
    return
  }

  const { step, field } = constantEdit.value
  const wire = remove ? undefined : toWire({ ...field, required: false, default: undefined }, constantValue.value)
  update(editing.setConstant(form.value, step, field.name, remove ? undefined : (wire ?? null)))
  constantEdit.value = null
}

// Lista editável (alternativa acessível ao canvas).
function sourcesFor(index: number, field: FieldDefinition) {
  if (!form.value) {
    return []
  }

  const options: { value: string; title: string }[] = [{ value: '', title: t('pipelines.editor.implicitOrNone') }]
  for (const input of form.value.inputs ?? []) {
    if (fieldTypes.assignable(input, field)) {
      options.push({ value: `pipeline|${input.name}`, title: `$pipeline.${input.name}` })
    }
  }

  ;(form.value.steps ?? []).slice(0, index).forEach((step) => {
    for (const output of contracts.value.get(contractKey(step.stageKey, step.stageVersion))?.outputs ?? []) {
      if (fieldTypes.assignable(output, field)) {
        options.push({ value: `step|${step.key}|${output.name}`, title: `$steps.${step.key}.${output.name}` })
      }
    }
  })
  return options
}

function currentSource(stepKey: string, input: string): string {
  const source = form.value?.steps?.find((s) => s.key === stepKey)?.bindings?.find((b) => b.input === input)?.source
  return source?.kind === 'pipeline'
    ? `pipeline|${source.field}`
    : source?.kind === 'step'
      ? `step|${source.step}|${source.field}`
      : ''
}

function chooseSource(stepKey: string, input: string, value: string) {
  if (!form.value) {
    return
  }

  const [kind, a, b] = value.split('|')
  const source: BindingSource | null =
    kind === 'pipeline' ? { kind: 'pipeline', field: a! } : kind === 'step' ? { kind: 'step', step: a!, field: b! } : null
  update(editing.bind(form.value, stepKey, input, source))
}

// Painel lateral.
const selectedStep = computed(() => {
  if (!form.value || !selection.value?.startsWith('step:')) {
    return null
  }

  return form.value.steps?.find((s) => stepNodeId(s.key) === selection.value) ?? null
})
const stepVersions = ref<number[]>([])
watch(selectedStep, async (step) => {
  stepVersions.value = []
  if (step) {
    const detail = await unwrap(api.GET('/stages/{key}', { params: { path: { key: step.stageKey } } })).catch(() => null)
    stepVersions.value = (detail?.versions ?? []).filter((v) => v.status === 'Published').map((v) => v.version)
  }
})

function renameStep(from: string, to: string) {
  if (form.value && editing.isValidStepKey(to)) {
    const next = editing.renameStep(form.value, from, to)
    if (next !== form.value) {
      const { [stepNodeId(from)]: position, ...rest } = layout.value.nodes
      layout.value = { nodes: position ? { ...rest, [stepNodeId(to)]: position } : rest }
      selection.value = stepNodeId(to)
      update(next)
    }
  }
}

function setStep(stepKey: string, changes: { displayName?: string | null; stageVersion?: number }) {
  if (!form.value) {
    return
  }

  const next = structuredClone(form.value)
  const step = next.steps?.find((s) => s.key === stepKey)
  if (step) {
    Object.assign(step, changes)
    update(next)
    if (changes.stageVersion) {
      void loadContracts()
    }
  }
}

function setOutput(index: number, changes: { name?: string; description?: string | null }) {
  if (!form.value) {
    return
  }

  const next = structuredClone(form.value)
  const output = next.outputs?.[index]
  if (output) {
    Object.assign(output, changes)
    update(next)
  }
}

// Validação contínua (com debounce).
let validateTimer: ReturnType<typeof setTimeout> | null = null
watch(
  form,
  (value) => {
    if (!value) {
      return
    }

    if (validateTimer) {
      clearTimeout(validateTimer)
    }

    validateTimer = setTimeout(async () => {
      validating.value = true
      try {
        report.value = await unwrap(api.POST('/pipelines/validate', { body: withLayout(value) }))
      } catch {
        // A validação contínua é auxiliar; erros de rede aparecem no salvar/publicar.
      } finally {
        validating.value = false
      }
    }, 600)
  },
  { deep: true },
)

function focusIssue(issue: Schemas['ValidationIssue']) {
  if (issue.step && view.value === 'canvas') {
    canvas.value?.focus(stepNodeId(issue.step))
    selection.value = stepNodeId(issue.step)
  }
}

function restore() {
  if (local.offer.value) {
    const data = local.offer.value.data
    form.value = { ...structuredClone(data), layout: undefined }
    layout.value = readLayout(data.layout)
    local.offer.value = null
    void loadContracts()
  }
}

async function save(force?: Draft): Promise<boolean> {
  if (!form.value || !draft.value) {
    return false
  }

  if (!dirty.value && !force) {
    return true
  }

  saving.value = true
  try {
    take(
      await unwrapWithETag(
        api.PUT('/pipelines/{key}/draft', {
          params: { path: { key: key.value } },
          body: withLayout(form.value),
          headers: { 'If-Match': force ? `"${force.rowVersion}"` : etag.value },
        }),
      ),
    )
    local.saved()
    notify.success(t('editor.saved'))
    return true
  } catch (error) {
    if (error instanceof ApiError && error.code === 'CONCURRENCY_CONFLICT') {
      conflict.value = error.extension<Draft>('current') ?? null
    } else if (!(error instanceof ApiError && (error.status === 401 || error.status === 403))) {
      notify.error(errorText(error))
    }

    return false
  } finally {
    saving.value = false
  }
}

function reloadFromConflict() {
  if (conflict.value) {
    local.keep()
    take({ data: conflict.value, etag: `"${conflict.value.rowVersion}"` })
    void loadContracts()
  }

  conflict.value = null
}

async function overwrite() {
  const current = conflict.value
  conflict.value = null
  if (current) {
    await save(current)
  }
}

async function runTest() {
  if (!form.value || !testForm.value?.check() || !(await save())) {
    return
  }

  testing.value = true
  try {
    const body = toFormData(collectInputs(form.value.inputs ?? [], testValues.value))
    const response = await ensureOk(
      await apiFetch(`/pipelines/${encodeURIComponent(key.value)}/draft/test`, { method: 'POST', body }),
    )
    const handle = (await response.json()) as Schemas['RunHandle']
    testOpen.value = false
    await router.push({ name: 'run', params: { id: handle.runId } })
  } catch (error) {
    notify.error(errorText(error))
  } finally {
    testing.value = false
  }
}

async function publish() {
  publishError.value = null
  if (!(await save())) {
    return
  }

  publishing.value = true
  try {
    publication.value = (
      await unwrapWithETag(
        api.POST('/pipelines/{key}/draft/publish', {
          params: { path: { key: key.value } },
          headers: { 'If-Match': etag.value },
        }),
      )
    ).data
    local.saved()
  } catch (error) {
    if (error instanceof ApiError && error.code === 'PIPELINE_INVALID') {
      report.value = error.extension<Schemas['ValidationReport']>('report') ?? report.value
      publishError.value = t('pipelines.editor.invalid')
    } else if (error instanceof ApiError && error.code === 'CONCURRENCY_CONFLICT') {
      conflict.value = error.extension<Draft>('current') ?? null
    } else if (!(error instanceof ApiError && (error.status === 401 || error.status === 403))) {
      publishError.value = errorText(error)
    }
  } finally {
    publishing.value = false
  }
}

async function discard() {
  try {
    await unwrapWithETag(
      api.DELETE('/pipelines/{key}/draft', { params: { path: { key: key.value } }, headers: { 'If-Match': etag.value } }),
    )
    local.saved()
    form.value = null
    draft.value = null
    await router.push({ name: 'pipeline', params: { key: key.value } })
  } catch (error) {
    notify.error(errorText(error))
  } finally {
    confirmDiscard.value = false
  }
}

function finishPublish() {
  form.value = null
  draft.value = null
  void router.push({ name: 'pipeline', params: { key: key.value } })
}
</script>

<template>
  <v-container fluid>
    <PageHeader
      :title="t('pipelines.editor.title')"
      :subtitle="draft ? t('stages.editor.subtitle', { key, version: draft.version }) : key"
    >
      <template v-if="form">
        <v-chip v-if="dirty" color="warning" size="small" label data-testid="dirty">{{ t('editor.unsaved') }}</v-chip>
        <v-btn variant="tonal" prepend-icon="mdi-content-save" :loading="saving" data-testid="save" @click="save()">{{
          t('common.save')
        }}</v-btn>
        <v-btn variant="text" prepend-icon="mdi-play-circle-outline" data-testid="open-test" @click="testOpen = true">{{
          t('editor.test')
        }}</v-btn>
        <v-btn
          color="primary"
          prepend-icon="mdi-rocket-launch-outline"
          :loading="publishing"
          data-testid="publish"
          @click="publish"
          >{{ t('editor.publish') }}</v-btn
        >
        <v-btn
          variant="text"
          color="error"
          icon="mdi-delete-outline"
          :aria-label="t('editor.discard')"
          @click="confirmDiscard = true"
        />
      </template>
    </PageHeader>

    <v-alert v-if="missing" type="info" variant="tonal">{{ t('pipelines.editor.noDraft') }}</v-alert>
    <v-alert v-if="local.offer.value" type="info" variant="tonal" class="mb-4" data-testid="restore-offer">
      {{ t('drafts.offer', { when: formatDateTime(local.offer.value.savedAt) }) }}
      <template #append>
        <v-btn variant="text" @click="restore">{{ t('drafts.restore') }}</v-btn>
        <v-btn variant="text" @click="local.discard()">{{ t('drafts.discard') }}</v-btn>
      </template>
    </v-alert>
    <v-alert v-if="publishing" type="info" variant="tonal" class="mb-4">
      <v-progress-circular indeterminate size="18" width="2" class="mr-2" />{{ t('pipelines.editor.publishing') }}
    </v-alert>
    <v-alert
      v-if="publishError"
      type="error"
      variant="tonal"
      class="mb-4"
      closable
      data-testid="publish-error"
      @click:close="publishError = null"
      >{{ publishError }}</v-alert
    >

    <v-row v-if="form" density="compact">
      <v-col cols="12" lg="2">
        <v-card :title="t('pipelines.editor.palette')" density="compact">
          <v-card-text class="pb-0">
            <v-text-field
              v-model="paletteSearch"
              :label="t('common.search')"
              prepend-inner-icon="mdi-magnify"
              density="compact"
              hide-details
              clearable
            />
          </v-card-text>
          <v-list density="compact" class="pipeline-editor__palette" data-testid="palette">
            <v-list-item
              v-for="stage in palette"
              :key="stage.key"
              draggable="true"
              :data-stage="stage.key"
              :title="stage.name"
              :subtitle="stage.key"
              @dragstart="dragStage($event, stage)"
            >
              <template #append>
                <v-select
                  :model-value="paletteChoice[stage.key] ?? stage.latestPublishedVersion"
                  :items="
                    (paletteVersions[stage.key] ?? [stage.latestPublishedVersion]).map((v) => ({
                      value: v,
                      title: `v${v}`,
                    }))
                  "
                  density="compact"
                  variant="plain"
                  hide-details
                  :aria-label="t('pipelines.editor.version')"
                  style="max-width: 72px"
                  @focus="paletteVersionsOf(stage)"
                  @update:model-value="(v: number) => (paletteChoice = { ...paletteChoice, [stage.key]: v })"
                />
                <v-btn
                  icon="mdi-plus"
                  size="x-small"
                  variant="text"
                  :aria-label="t('pipelines.editor.addStep', { stage: stage.key })"
                  @click="addFromPalette(stage)"
                />
              </template>
            </v-list-item>
            <v-list-item v-if="palette.length === 0" :subtitle="t('pipelines.editor.noStages')" />
          </v-list>
        </v-card>
      </v-col>

      <v-col cols="12" lg="7">
        <v-card>
          <div class="d-flex align-center flex-wrap ga-2 pa-2">
            <v-btn-toggle v-model="view" mandatory density="compact" color="primary">
              <v-btn value="canvas" prepend-icon="mdi-graph-outline">{{ t('pipelines.editor.canvas') }}</v-btn>
              <v-btn value="list" prepend-icon="mdi-format-list-bulleted">{{ t('pipelines.editor.list') }}</v-btn>
            </v-btn-toggle>
            <v-spacer />
            <v-btn v-if="view === 'canvas'" size="small" variant="text" prepend-icon="mdi-auto-fix" @click="autoArrange">{{
              t('pipelines.editor.autoLayout')
            }}</v-btn>
          </div>
          <div
            class="d-flex flex-wrap align-center ga-1 px-2 pb-2"
            :aria-label="t('pipelines.editor.order')"
            data-testid="order-bar"
          >
            <span class="text-caption text-medium-emphasis mr-1">{{ t('pipelines.editor.order') }}:</span>
            <v-chip
              v-for="(step, index) in form.steps ?? []"
              :key="step.key"
              size="small"
              label
              :draggable="true"
              :data-order="index + 1"
              @dragstart="dragIndex = index"
              @dragover.prevent
              @drop="dropOrder(index)"
              @click="selection = stepNodeId(step.key)"
            >
              {{ index + 1 }}. {{ step.key }}
            </v-chip>
          </div>
          <FlowCanvas
            v-if="view === 'canvas'"
            ref="canvas"
            :nodes="graph.nodes"
            :edges="graph.edges"
            :editable="true"
            :accepts="accepts"
            :label="t('pipelines.canvas')"
            height="calc(100vh - 300px)"
            data-testid="pipeline-canvas"
            @connect="onConnect"
            @remove-edges="onRemoveEdges"
            @remove-nodes="onRemoveNodes"
            @moved="onMoved"
            @drop="onDrop"
            @input-clicked="onInputClicked"
            @node-clicked="(id: string) => (selection = id)"
          />
          <div v-else class="pa-3" data-testid="pipeline-list">
            <v-card v-for="(step, index) in form.steps ?? []" :key="step.key" variant="outlined" class="mb-3">
              <v-card-title class="text-body-1"
                >{{ index + 1 }}. {{ step.key }}
                <span class="text-caption text-medium-emphasis">({{ step.stageKey }}@{{ step.stageVersion }})</span></v-card-title
              >
              <v-card-text>
                <div
                  v-for="field in contracts.get(contractKey(step.stageKey, step.stageVersion))?.inputs ?? []"
                  :key="field.name"
                  class="d-flex align-center ga-2 mb-2"
                >
                  <span class="text-body-2" style="min-width: 160px"
                    >{{ field.name }} <span class="text-caption text-medium-emphasis">({{ typeLabel(field) }})</span></span
                  >
                  <v-select
                    :model-value="currentSource(step.key, field.name)"
                    :items="sourcesFor(index, field)"
                    :label="t('pipelines.editor.source')"
                    density="compact"
                    hide-details
                    @update:model-value="(v: string) => chooseSource(step.key, field.name, v)"
                  />
                  <v-btn
                    size="small"
                    variant="text"
                    icon="mdi-pin-outline"
                    :aria-label="t('flow.constant')"
                    @click="onInputClicked(stepNodeId(step.key), field.name)"
                  />
                </div>
                <div class="d-flex ga-1">
                  <v-btn
                    size="small"
                    variant="text"
                    icon="mdi-arrow-up"
                    :disabled="index === 0"
                    :aria-label="t('fieldBuilder.moveUp')"
                    @click="update(editing.moveStep(form, index, index - 1))"
                  />
                  <v-btn
                    size="small"
                    variant="text"
                    icon="mdi-arrow-down"
                    :disabled="index === (form.steps?.length ?? 0) - 1"
                    :aria-label="t('fieldBuilder.moveDown')"
                    @click="update(editing.moveStep(form, index, index + 1))"
                  />
                  <v-spacer />
                  <v-btn
                    size="small"
                    variant="text"
                    color="error"
                    prepend-icon="mdi-delete-outline"
                    @click="update(editing.removeStep(form, step.key))"
                    >{{ t('pipelines.editor.removeStep') }}</v-btn
                  >
                </div>
              </v-card-text>
            </v-card>
            <p v-if="(form.steps ?? []).length === 0" class="text-medium-emphasis">
              {{ t('pipelines.editor.emptyList') }}
            </p>
          </div>
        </v-card>
      </v-col>

      <v-col cols="12" lg="3">
        <v-card class="mb-3">
          <v-card-title class="text-body-1">
            {{
              selection === inputsNode
                ? t('flow.inputs')
                : selection === outputsNode
                  ? t('flow.outputs')
                  : selectedStep
                    ? selectedStep.key
                    : t('pipelines.editor.selection')
            }}
          </v-card-title>
          <v-card-text>
            <template v-if="selection === inputsNode">
              <FieldBuilder
                :model-value="form.inputs ?? []"
                kind="inputs"
                @update:model-value="(v: FieldDefinition[]) => form && update({ ...form, inputs: v })"
              />
            </template>
            <template v-else-if="selection === outputsNode">
              <p v-if="(form.outputs ?? []).length === 0" class="text-medium-emphasis text-body-2">
                {{ t('pipelines.editor.outputsHelp') }}
              </p>
              <div v-for="(output, index) in form.outputs ?? []" :key="index" class="mb-3">
                <v-text-field
                  :model-value="output.name"
                  :label="t('fieldBuilder.name')"
                  density="compact"
                  @update:model-value="(v: string) => setOutput(index, { name: v })"
                />
                <div class="text-caption text-medium-emphasis">$steps.{{ output.source.step }}.{{ output.source.field }}</div>
                <v-btn size="small" variant="text" color="error" @click="onRemoveEdges([`out:${output.name}`])">{{
                  t('fieldBuilder.remove')
                }}</v-btn>
              </div>
            </template>
            <template v-else-if="selectedStep">
              <v-text-field
                :model-value="selectedStep.key"
                :label="t('pipelines.editor.stepKey')"
                density="compact"
                @change="(e: Event) => renameStep(selectedStep!.key, (e.target as HTMLInputElement).value)"
              />
              <v-text-field
                :model-value="selectedStep.displayName ?? ''"
                :label="t('pipelines.editor.displayName')"
                density="compact"
                @update:model-value="(v: string) => setStep(selectedStep!.key, { displayName: v || null })"
              />
              <v-select
                :model-value="selectedStep.stageVersion"
                :items="
                  (stepVersions.length ? stepVersions : [selectedStep.stageVersion]).map((v) => ({
                    value: v,
                    title: `${selectedStep!.stageKey}@${v}`,
                  }))
                "
                :label="t('pipelines.editor.version')"
                density="compact"
                @update:model-value="(v: number) => setStep(selectedStep!.key, { stageVersion: v })"
              />
              <v-btn
                variant="text"
                color="error"
                prepend-icon="mdi-delete-outline"
                @click="(update(editing.removeStep(form, selectedStep!.key)), (selection = null))"
              >
                {{ t('pipelines.editor.removeStep') }}
              </v-btn>
            </template>
            <p v-else class="text-medium-emphasis text-body-2">{{ t('pipelines.editor.selectHelp') }}</p>
          </v-card-text>
        </v-card>
        <v-card>
          <v-card-title class="text-body-1 d-flex align-center">
            {{ t('stages.editor.problems') }}
            <v-progress-circular v-if="validating" indeterminate size="16" width="2" class="ml-2" />
          </v-card-title>
          <ProblemList :report="report" @select="focusIssue" />
        </v-card>
      </v-col>
    </v-row>

    <v-dialog
      :model-value="constantEdit !== null"
      max-width="520"
      :aria-label="
        constantEdit ? t('pipelines.editor.inputTitle', { input: constantEdit.field.name, step: constantEdit.step }) : undefined
      "
      @update:model-value="(open: boolean) => !open && (constantEdit = null)"
    >
      <v-card
        v-if="constantEdit && constantInfo"
        :title="t('pipelines.editor.inputTitle', { input: constantEdit.field.name, step: constantEdit.step })"
        data-testid="input-dialog"
      >
        <v-card-text>
          <p class="text-body-2 mb-3">
            {{ t('pipelines.editor.currentSource') }}:
            <strong>{{
              constantInfo.binding
                ? describe(constantInfo.binding.source)
                : constantInfo.implicit
                  ? `${describe(constantInfo.implicit)} (${t('pipelines.editor.implicit')})`
                  : t('pipelines.editor.unbound')
            }}</strong>
          </p>
          <FieldInput
            v-if="constantEdit.field.type !== 'file'"
            v-model="constantValue"
            :field="{ ...constantEdit.field, required: false }"
          />
          <p v-else class="text-medium-emphasis">{{ t('pipelines.editor.fileNoConstant') }}</p>
        </v-card-text>
        <v-card-actions>
          <v-btn v-if="constantInfo.binding" variant="text" color="error" @click="applyConstant(true)">{{
            t('pipelines.editor.clearBinding')
          }}</v-btn>
          <v-spacer />
          <v-btn variant="text" @click="constantEdit = null">{{ t('common.cancel') }}</v-btn>
          <v-btn v-if="constantEdit.field.type !== 'file'" color="primary" data-testid="set-constant" @click="applyConstant()">{{
            t('pipelines.editor.setConstant')
          }}</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-dialog v-model="testOpen" max-width="680" scrollable :aria-label="t('pipelines.editor.testTitle')">
      <v-card v-if="form" :title="t('pipelines.editor.testTitle')" data-testid="test-dialog">
        <v-card-text>
          <p class="text-medium-emphasis text-body-2 mb-3">{{ t('pipelines.editor.testHelp') }}</p>
          <FieldForm ref="testForm" v-model="testValues" :fields="form.inputs ?? []" />
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="testOpen = false">{{ t('common.cancel') }}</v-btn>
          <v-btn color="primary" prepend-icon="mdi-play" :loading="testing" data-testid="run-test" @click="runTest">{{
            t('stages.test.run')
          }}</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-dialog
      :model-value="publication !== null"
      max-width="560"
      persistent
      :aria-label="publication ? t('pipelines.editor.published', { version: publication.version.version }) : undefined"
    >
      <v-card
        v-if="publication"
        :title="t('pipelines.editor.published', { version: publication.version.version })"
        data-testid="published"
      >
        <v-card-text>
          <p v-if="publication.warnings.length === 0">{{ t('pipelines.editor.noWarnings') }}</p>
          <v-alert
            v-for="(warning, i) in publication.warnings"
            :key="i"
            type="warning"
            variant="tonal"
            density="compact"
            class="mb-2"
          >
            {{ warning.message }} <span class="text-caption">({{ warning.code }})</span>
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn color="primary" @click="finishPublish">{{ t('pipelines.editor.view') }}</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <ConflictDialog
      :model-value="conflict !== null"
      :mine="form"
      :current="conflict?.data"
      :author="conflict?.authorship.updatedBy"
      :when="conflict ? formatDateTime(conflict.authorship.updatedAt) : null"
      @update:model-value="(open: boolean) => !open && (conflict = null)"
      @reload="reloadFromConflict"
      @overwrite="overwrite"
    />
    <ConfirmDialog
      v-model="confirmDiscard"
      :title="t('editor.discardTitle')"
      :text="t('editor.discardText')"
      :confirm="t('editor.discard')"
      color="error"
      @confirm="discard"
    />
  </v-container>
</template>

<style scoped>
.pipeline-editor__palette {
  max-height: calc(100vh - 320px);
  overflow-y: auto;
}
.pipeline-editor__palette :deep(.v-list-item) {
  cursor: grab;
}
</style>
