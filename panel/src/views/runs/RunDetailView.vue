<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { api, apiFetch, unwrap, type Schemas } from '@/api/client'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import FieldOutput from '@/components/fields/FieldOutput.vue'
import FileCard from '@/components/fields/FileCard.vue'
import type { FieldDefinition } from '@/components/fields/values'
import { contractKey, outputField, type PipelineData, type StageContract } from '@/components/flow/model'
import { useApiAction } from '@/composables/useApiAction'
import { useContracts } from '@/composables/useContracts'
import { throttle, useLive, useNow } from '@/composables/useLive'
import { useNotify } from '@/stores/notify'
import { useSession } from '@/stores/session'
import { elapsedMs, formatBytes, formatDateTime, formatDuration, isActive, timeSpanMs } from '@/utils/format'

const RunFlow = defineAsyncComponent(() => import('@/components/flow/RunFlow.vue'))

/**
 * Detalhe de uma execução (spec 008): fluxo ao vivo, abas de entradas, saídas, passos, logs, arquivos e linha do tempo;
 * Cancelar e Reprocessar para o Executor.
 */
const { t } = useI18n()
const route = useRoute()
const session = useSession()
const notify = useNotify()
const contracts = useContracts()
const now = useNow()
const { run: call } = useApiAction()
const id = computed(() => String(route.params.id))

const run = ref<Schemas['RunSnapshot'] | null>(null)
const definition = shallowRef<PipelineData | null>(null)
const contractMap = shallowRef(new Map<string, StageContract>())
const missing = ref(false)
const tab = ref('inputs')
const confirmCancel = ref(false)
const confirmReprocess = ref(false)
const acting = ref(false)

async function load() {
  try {
    run.value = await unwrap(api.GET('/runs/{id}', { params: { path: { id: id.value } } }))
    missing.value = false
  } catch {
    missing.value = run.value === null
    return
  }

  if (!definition.value) {
    await loadDefinition(run.value)
  }
}

/** Definição para o canvas e para os tipos: a versão da trilha ou um passo único da Etapa. */
async function loadDefinition(snapshot: Schemas['RunSnapshot']) {
  if (snapshot.kind === 'Pipeline' || snapshot.kind === 'PipelineTest') {
    const version = await call(() =>
      unwrap(
        api.GET('/pipelines/{key}/versions/{version}', {
          params: { path: { key: snapshot.targetKey, version: snapshot.targetVersion } },
        }),
      ),
    )
    if (!version) {
      return
    }

    contractMap.value = await contracts.loadAll(version.data.steps ?? [])
    definition.value = version.data
    return
  }

  const contract = await contracts.load(snapshot.targetKey, snapshot.targetVersion)
  const step = snapshot.steps[0]?.stepKey ?? snapshot.targetKey
  contractMap.value = contract ? new Map([[contractKey(snapshot.targetKey, snapshot.targetVersion), contract]]) : new Map()
  definition.value = {
    inputs: contract?.inputs ?? [],
    steps: [{ key: step, stageKey: snapshot.targetKey, stageVersion: snapshot.targetVersion, bindings: [] }],
    outputs: (contract?.outputs ?? []).map((o) => ({ name: o.name, source: { step, field: o.name } })),
  }
}

const reload = throttle(() => void load(), 300)
const { restart } = useLive(() => [{ kind: 'run', id: id.value }], { onRun: reload, onStep: reload, poll: reload })
watch(id, () => {
  definition.value = null
  run.value = null
  restart()
  void load()
})
onMounted(load)

const inputFields = computed<FieldDefinition[]>(() => definition.value?.inputs ?? [])
const outputFields = computed<FieldDefinition[]>(() =>
  definition.value ? (definition.value.outputs ?? []).map((o) => outputField(definition.value!, o, contractMap.value)) : [],
)
const canCancel = computed(
  () => run.value !== null && isActive(run.value.status) && run.value.status !== 'Cancelling' && session.hasRole('Executor'),
)
const canReprocess = computed(() => run.value?.status === 'Failed' && session.hasRole('Executor'))
const isTest = computed(() => run.value?.kind === 'StageTest' || run.value?.kind === 'PipelineTest')
const waitingWorker = computed(
  () =>
    isTest.value &&
    run.value?.status === 'Queued' &&
    now.value - new Date(run.value.queuedAt ?? run.value.createdAt).getTime() >= 15_000,
)
const attempts = computed(() => (run.value?.steps ?? []).flatMap((step) => step.attempts.map((attempt) => ({ step, attempt }))))

async function cancel() {
  acting.value = true
  try {
    await call(async () => {
      await unwrap(api.POST('/runs/{id}/cancel', { params: { path: { id: id.value } } }))
      notify.info(t('runs.cancelRequested'))
    })
  } finally {
    acting.value = false
    confirmCancel.value = false
    reload()
  }
}

async function reprocess() {
  acting.value = true
  try {
    await call(async () => {
      await unwrap(api.POST('/runs/{id}/reprocess', { params: { path: { id: id.value } } }))
      notify.success(t('runs.reprocessQueued'))
    })
  } finally {
    acting.value = false
    confirmReprocess.value = false
    reload()
  }
}

// Logs por tentativa (stdout/stderr), com busca e aviso de truncamento.
const logAttempt = ref<string | null>(null)
const logStream = ref<'stdout' | 'stderr'>('stdout')
const logText = ref<string | null>(null)
const logInfo = ref<{ truncated: boolean; original: number } | null>(null)
const logSearch = ref('')
const logLoading = ref(false)

async function loadLog() {
  if (!logAttempt.value) {
    return
  }

  logLoading.value = true
  try {
    const response = await apiFetch(`/runs/${id.value}/attempts/${logAttempt.value}/logs/${logStream.value}`)
    if (response.ok) {
      logText.value = await response.text()
      logInfo.value = {
        truncated: response.headers.get('X-PSR-Log-Truncated') === 'true',
        original: Number(response.headers.get('X-PSR-Log-Original-Bytes') ?? 0),
      }
    } else {
      logText.value = null
      logInfo.value = null
    }
  } finally {
    logLoading.value = false
  }
}

watch([logAttempt, logStream], loadLog)
watch(attempts, (list) => {
  if (!logAttempt.value && list.length > 0) {
    logAttempt.value = list[list.length - 1]!.attempt.id
  }
})
const logLines = computed(() => {
  const lines = (logText.value ?? '').split('\n')
  const term = logSearch.value.trim().toLowerCase()
  return term ? lines.filter((line) => line.toLowerCase().includes(term)) : lines
})

// Linha do tempo (Gantt) dos passos e tentativas.
const timeline = computed(() => {
  const start = run.value?.startedAt ?? run.value?.createdAt
  if (!run.value || !start) {
    return []
  }

  const origin = new Date(start).getTime()
  const end = Math.max(
    run.value.finishedAt ? new Date(run.value.finishedAt).getTime() : now.value,
    ...attempts.value.map(({ attempt }) => (attempt.finishedAt ? new Date(attempt.finishedAt).getTime() : now.value)),
  )
  const total = Math.max(1, end - origin)
  return attempts.value.map(({ step, attempt }) => {
    const from = new Date(attempt.startedAt).getTime() - origin
    const to = (attempt.finishedAt ? new Date(attempt.finishedAt).getTime() : now.value) - origin
    return {
      key: attempt.id,
      label: `${step.order}. ${step.stepKey} #${attempt.number}`,
      left: `${(Math.max(0, from) / total) * 100}%`,
      width: `${Math.max(0.5, ((to - from) / total) * 100)}%`,
      status: attempt.status,
      duration: formatDuration(to - from),
    }
  })
})
</script>

<template>
  <v-container fluid>
    <v-alert v-if="missing" type="warning" variant="tonal">{{ t('runs.notFound') }}</v-alert>
    <template v-if="run">
      <PageHeader :title="`${run.targetKey}@${run.targetVersion}`">
        <template #subtitle>
          <div class="d-flex flex-wrap align-center ga-2 mt-1">
            <StatusChip :status="run.status" data-testid="run-status" />
            <span class="text-body-2">{{ t(`kind.${run.kind}`) }}</span>
            <span class="text-body-2 text-medium-emphasis">· {{ t(`trigger.${run.trigger}`) }} ({{ run.triggeredBy }})</span>
            <span class="text-body-2 text-medium-emphasis">· {{ formatDateTime(run.createdAt) }}</span>
            <span class="text-body-2 text-medium-emphasis"
              >· {{ formatDuration(elapsedMs(run.startedAt, run.finishedAt, now)) }}</span
            >
            <span v-if="run.correlationId" class="text-body-2 text-medium-emphasis"
              >· {{ t('runs.correlation') }}: {{ run.correlationId }}</span
            >
          </div>
        </template>
        <v-btn
          v-if="canCancel"
          color="error"
          variant="tonal"
          prepend-icon="mdi-cancel"
          data-testid="cancel-run"
          @click="confirmCancel = true"
        >
          {{ t('runs.cancel') }}
        </v-btn>
        <v-btn
          v-if="canReprocess"
          color="primary"
          prepend-icon="mdi-replay"
          data-testid="reprocess-run"
          @click="confirmReprocess = true"
        >
          {{ t('runs.reprocess') }}
        </v-btn>
      </PageHeader>

      <v-alert v-if="waitingWorker" type="warning" variant="tonal" class="mb-4" data-testid="no-worker">{{
        t('runs.noWorker')
      }}</v-alert>
      <v-alert v-if="run.error" type="error" variant="tonal" class="mb-4" data-testid="run-error">
        <div class="font-weight-medium">{{ run.error.message }}</div>
        <div class="text-caption">
          {{ t(`errorKind.${run.error.kind}`) }}<template v-if="run.error.code"> · {{ run.error.code }}</template
          ><template v-if="run.error.stepKey"> · {{ t('runs.step') }} {{ run.error.stepKey }}</template>
        </div>
        <details v-if="run.error.traceback" class="mt-2">
          <summary>{{ t('runs.traceback') }}</summary>
          <pre class="run-detail__pre">{{ run.error.traceback }}</pre>
        </details>
      </v-alert>

      <RunFlow v-if="definition" :run="run" :definition="definition" :contracts="contractMap" class="mb-4" />

      <v-card>
        <v-tabs v-model="tab" show-arrows>
          <v-tab value="inputs">{{ t('runs.tabs.inputs') }}</v-tab>
          <v-tab value="outputs">{{ t('runs.tabs.outputs') }}</v-tab>
          <v-tab value="steps">{{ t('runs.tabs.steps') }}</v-tab>
          <v-tab value="logs">{{ t('runs.tabs.logs') }}</v-tab>
          <v-tab value="files">{{ t('runs.tabs.files') }}</v-tab>
          <v-tab value="timeline">{{ t('runs.tabs.timeline') }}</v-tab>
        </v-tabs>
        <v-divider />
        <v-window v-model="tab">
          <v-window-item value="inputs">
            <v-table density="compact">
              <tbody>
                <tr v-for="field in inputFields" :key="field.name">
                  <th class="w-25">{{ field.name }}</th>
                  <td>
                    <FieldOutput :field="field" :value="run.inputs[field.name]" :artifacts="run.artifacts" />
                  </td>
                </tr>
              </tbody>
            </v-table>
          </v-window-item>
          <v-window-item value="outputs">
            <p v-if="!run.outputs" class="pa-4 text-medium-emphasis">{{ t('runs.noOutputs') }}</p>
            <v-table v-else density="compact" data-testid="run-outputs">
              <tbody>
                <tr v-for="field in outputFields" :key="field.name" :data-output="field.name">
                  <th class="w-25">{{ field.name }}</th>
                  <td>
                    <FieldOutput :field="field" :value="run.outputs[field.name]" :artifacts="run.artifacts" />
                  </td>
                </tr>
              </tbody>
            </v-table>
          </v-window-item>
          <v-window-item value="steps">
            <v-table density="compact" data-testid="run-steps">
              <thead>
                <tr>
                  <th>{{ t('runs.columns.step') }}</th>
                  <th>{{ t('runs.columns.attempt') }}</th>
                  <th>{{ t('runs.columns.cause') }}</th>
                  <th>{{ t('runs.columns.status') }}</th>
                  <th>{{ t('runs.columns.worker') }}</th>
                  <th>{{ t('runs.columns.duration') }}</th>
                  <th>{{ t('runs.columns.overhead') }}</th>
                </tr>
              </thead>
              <tbody>
                <template v-for="step in run.steps" :key="step.id">
                  <tr :data-step="step.stepKey">
                    <td>
                      <strong>{{ step.order }}. {{ step.stepKey }}</strong>
                      <div class="text-caption text-medium-emphasis">{{ step.stageKey }}@{{ step.stageVersion }}</div>
                    </td>
                    <td>{{ step.currentAttempt }}</td>
                    <td />
                    <td><StatusChip :status="step.status" kind="step" /></td>
                    <td />
                    <td>{{ formatDuration(elapsedMs(step.startedAt, step.finishedAt, now)) }}</td>
                    <td />
                  </tr>
                  <tr
                    v-for="attempt in step.attempts"
                    :key="attempt.id"
                    class="text-medium-emphasis"
                    :data-attempt="attempt.number"
                  >
                    <td class="pl-8">↳ {{ attempt.triggeredBy }}</td>
                    <td>#{{ attempt.number }}</td>
                    <td>{{ t(`cause.${attempt.cause}`) }}</td>
                    <td><StatusChip :status="attempt.status" kind="attempt" /></td>
                    <td>{{ attempt.workerId ?? '—' }}</td>
                    <td>
                      {{ formatDuration(timeSpanMs(attempt.duration) ?? elapsedMs(attempt.startedAt, attempt.finishedAt, now)) }}
                    </td>
                    <td>{{ formatDuration(timeSpanMs(attempt.overhead)) }}</td>
                  </tr>
                </template>
              </tbody>
            </v-table>
          </v-window-item>
          <v-window-item value="logs">
            <div class="pa-4">
              <div class="d-flex flex-wrap ga-2 mb-3">
                <v-select
                  v-model="logAttempt"
                  :items="
                    attempts.map(({ step, attempt }) => ({
                      value: attempt.id,
                      title: `${step.order}. ${step.stepKey} #${attempt.number}`,
                    }))
                  "
                  :label="t('runs.logs.attempt')"
                  hide-details
                  style="max-width: 320px"
                />
                <v-btn-toggle v-model="logStream" mandatory density="comfortable" color="primary">
                  <v-btn value="stdout">stdout</v-btn>
                  <v-btn value="stderr">stderr</v-btn>
                </v-btn-toggle>
                <v-text-field
                  v-model="logSearch"
                  :label="t('common.search')"
                  prepend-inner-icon="mdi-magnify"
                  hide-details
                  clearable
                  style="max-width: 280px"
                />
              </div>
              <v-alert v-if="logInfo?.truncated" type="info" variant="tonal" density="compact" class="mb-2">
                {{
                  t('runs.logs.truncated', {
                    shown: formatBytes((logText ?? '').length),
                    total: formatBytes(logInfo.original),
                  })
                }}
              </v-alert>
              <v-progress-linear v-if="logLoading" indeterminate />
              <pre v-if="logText !== null" class="run-detail__pre" data-testid="run-log">{{ logLines.join('\n') }}</pre>
              <p v-else-if="!logLoading" class="text-medium-emphasis">{{ t('runs.logs.empty') }}</p>
            </div>
          </v-window-item>
          <v-window-item value="files">
            <div class="pa-4">
              <p v-if="run.artifacts.length === 0" class="text-medium-emphasis">{{ t('runs.noFiles') }}</p>
              <template v-for="direction in ['Input', 'Output'] as const" :key="direction">
                <div v-if="run.artifacts.some((a) => a.direction === direction)" class="mb-3">
                  <div class="text-subtitle-2 mb-1">{{ t(`runs.files.${direction}`) }}</div>
                  <FileCard
                    v-for="artifact in run.artifacts.filter((a) => a.direction === direction)"
                    :key="artifact.id"
                    :artifact="artifact"
                  />
                </div>
              </template>
            </div>
          </v-window-item>
          <v-window-item value="timeline">
            <div class="pa-4" data-testid="run-timeline">
              <div v-for="bar in timeline" :key="bar.key" class="run-detail__gantt-row">
                <span class="run-detail__gantt-label text-caption">{{ bar.label }}</span>
                <div class="run-detail__gantt-track">
                  <div
                    class="run-detail__gantt-bar"
                    :class="`bg-${bar.status === 'Succeeded' ? 'success' : bar.status === 'Running' ? 'info' : 'error'}`"
                    :style="{ left: bar.left, width: bar.width }"
                    :title="bar.duration"
                  />
                </div>
                <span class="text-caption ml-2">{{ bar.duration }}</span>
              </div>
              <p v-if="timeline.length === 0" class="text-medium-emphasis">{{ t('runs.noAttempts') }}</p>
            </div>
          </v-window-item>
        </v-window>
      </v-card>

      <ConfirmDialog
        v-model="confirmCancel"
        :title="t('runs.cancelTitle')"
        :text="t('runs.cancelText')"
        :confirm="t('runs.cancel')"
        color="error"
        :busy="acting"
        @confirm="cancel"
      />
      <ConfirmDialog
        v-model="confirmReprocess"
        :title="t('runs.reprocessTitle')"
        :text="t('runs.reprocessText')"
        :confirm="t('runs.reprocess')"
        :busy="acting"
        @confirm="reprocess"
      />
    </template>
    <v-skeleton-loader v-else-if="!missing" type="article, table" />
  </v-container>
</template>

<style scoped>
.run-detail__pre {
  white-space: pre-wrap;
  word-break: break-word;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 12px;
  max-height: 480px;
  overflow: auto;
  background: rgba(var(--v-theme-on-surface), 0.04);
  padding: 8px;
  border-radius: 4px;
}
.run-detail__gantt-row {
  display: flex;
  align-items: center;
  margin-bottom: 6px;
}
.run-detail__gantt-label {
  width: 200px;
  flex-shrink: 0;
}
.run-detail__gantt-track {
  position: relative;
  flex: 1;
  height: 14px;
  background: rgba(var(--v-theme-on-surface), 0.06);
  border-radius: 3px;
}
.run-detail__gantt-bar {
  position: absolute;
  top: 0;
  bottom: 0;
  border-radius: 3px;
}
</style>
