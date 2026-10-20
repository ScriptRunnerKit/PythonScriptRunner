<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, apiFetch, ensureOk, unwrap, type Schemas } from '@/api/client'
import StatusChip from '@/components/common/StatusChip.vue'
import FieldForm from '@/components/fields/FieldForm.vue'
import FieldOutput from '@/components/fields/FieldOutput.vue'
import { collectInputs, toFormData, type FieldDefinition, type UiValue } from '@/components/fields/values'
import { useErrorText } from '@/composables/useApiAction'
import { throttle, useNow } from '@/composables/useLive'
import { useRealtime } from '@/stores/realtime'
import { elapsedMs, formatDuration, isActive, timeSpanMs } from '@/utils/format'

/**
 * Testar o rascunho de uma Etapa (D-49, D-114): formulário gerado pelas entradas, teste enfileirado para os workers,
 * status ao vivo, aviso após 15 s sem worker (D-147) e, ao final, saídas por tipo, stdout/stderr, duração e overhead.
 */
const props = defineProps<{
  stageKey: string
  inputs: FieldDefinition[]
  outputs: FieldDefinition[]
  beforeRun: () => Promise<boolean>
}>()
const { t } = useI18n()
const errorText = useErrorText()
const realtime = useRealtime()
const now = useNow()

const values = ref<Record<string, UiValue>>({})
const form = ref<InstanceType<typeof FieldForm> | null>(null)
const running = ref(false)
const error = ref<string | null>(null)
const run = ref<Schemas['RunSnapshot'] | null>(null)
const logs = ref<{ stdout: string; stderr: string }>({ stdout: '', stderr: '' })
let stop: (() => void) | null = null

const attempt = computed(() => run.value?.steps[0]?.attempts.at(-1) ?? null)
const waitingWorker = computed(
  () => run.value?.status === 'Queued' && now.value - new Date(run.value.queuedAt ?? run.value.createdAt).getTime() >= 15_000,
)

async function refresh() {
  if (!run.value) {
    return
  }

  run.value = await unwrap(api.GET('/runs/{id}', { params: { path: { id: run.value.id } } }))
  if (!isActive(run.value.status)) {
    stop?.()
    stop = null
    await loadLogs()
  }
}

async function loadLogs() {
  const current = attempt.value
  if (!run.value || !current) {
    return
  }

  for (const stream of ['stdout', 'stderr'] as const) {
    const response = await apiFetch(`/runs/${run.value.id}/attempts/${current.id}/logs/${stream}`)
    logs.value[stream] = response.ok ? await response.text() : ''
  }
}

async function start() {
  error.value = null
  if (!form.value?.check() || !(await props.beforeRun())) {
    return
  }

  running.value = true
  try {
    const body = toFormData(collectInputs(props.inputs, values.value))
    const response = await ensureOk(
      await apiFetch(`/stages/${encodeURIComponent(props.stageKey)}/draft/test`, { method: 'POST', body }),
    )
    const handle = (await response.json()) as Schemas['RunHandle']
    logs.value = { stdout: '', stderr: '' }
    run.value = await unwrap(api.GET('/runs/{id}', { params: { path: { id: handle.runId } } }))
    stop?.()
    const reload = throttle(() => void refresh(), 300)
    stop = realtime.watch([{ kind: 'run', id: handle.runId }], { onRun: reload, onStep: reload, poll: reload })
  } catch (e) {
    error.value = errorText(e)
  } finally {
    running.value = false
  }
}

onBeforeUnmount(() => stop?.())
</script>

<template>
  <div data-testid="stage-test">
    <p class="text-medium-emphasis text-body-2 mb-3">{{ t('stages.test.help') }}</p>
    <FieldForm ref="form" v-model="values" :fields="inputs" />
    <v-btn color="primary" prepend-icon="mdi-play" :loading="running" data-testid="run-test" @click="start">{{
      t('stages.test.run')
    }}</v-btn>
    <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mt-3">{{ error }}</v-alert>

    <template v-if="run">
      <v-divider class="my-4" />
      <div class="d-flex align-center flex-wrap ga-2 mb-2">
        <StatusChip :status="run.status" data-testid="test-status" />
        <span class="text-body-2">{{ formatDuration(elapsedMs(run.startedAt, run.finishedAt, now)) }}</span>
        <span v-if="attempt?.overhead" class="text-caption text-medium-emphasis"
          >· {{ t('stages.test.overhead', { value: formatDuration(timeSpanMs(attempt.overhead)) }) }}</span
        >
        <v-spacer />
        <router-link :to="{ name: 'run', params: { id: run.id } }" class="text-body-2">{{ t('stages.test.open') }}</router-link>
      </div>
      <v-alert v-if="waitingWorker" type="warning" variant="tonal" density="compact" data-testid="no-worker">{{
        t('runs.noWorker')
      }}</v-alert>
      <v-alert v-if="run.error" type="error" variant="tonal" density="compact" class="mb-2">{{ run.error.message }}</v-alert>
      <template v-if="run.outputs">
        <div class="text-subtitle-2 mb-1">{{ t('runs.tabs.outputs') }}</div>
        <v-table density="compact" data-testid="test-outputs">
          <tbody>
            <tr v-for="field in outputs" :key="field.name" :data-output="field.name">
              <th class="w-25">{{ field.name }}</th>
              <td><FieldOutput :field="field" :value="run.outputs[field.name]" :artifacts="run.artifacts" /></td>
            </tr>
          </tbody>
        </v-table>
      </template>
      <template v-if="logs.stdout || logs.stderr">
        <div class="text-subtitle-2 mt-3 mb-1">stdout</div>
        <pre class="stage-test__log" data-testid="test-stdout">{{ logs.stdout || '—' }}</pre>
        <div class="text-subtitle-2 mt-3 mb-1">stderr</div>
        <pre class="stage-test__log" data-testid="test-stderr">{{ logs.stderr || '—' }}</pre>
      </template>
    </template>
  </div>
</template>

<style scoped>
.stage-test__log {
  white-space: pre-wrap;
  word-break: break-word;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 12px;
  max-height: 220px;
  overflow: auto;
  background: rgba(var(--v-theme-on-surface), 0.04);
  padding: 8px;
  border-radius: 4px;
}
</style>
