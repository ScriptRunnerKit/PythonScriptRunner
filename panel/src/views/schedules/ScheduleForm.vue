<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, unwrap, type Schemas } from '@/api/client'
import FieldForm from '@/components/fields/FieldForm.vue'
import { collectInputs, fromWire, type FieldDefinition, type UiValue } from '@/components/fields/values'
import { useApiAction, useErrorText } from '@/composables/useApiAction'
import { formatDateTime } from '@/utils/format'

/**
 * Formulário de agendamento (spec 008): nome, cron com ajuda e prévia das próximas 5 ocorrências no fuso, fuso IANA
 * (padrão America/Sao_Paulo), versão fixa opcional e entradas fixas; trilhas com entrada `file` não agendam (D-89).
 */
const props = defineProps<{ schedule?: Schemas['ScheduleDetail'] | null; pipelineKey?: string }>()
const emit = defineEmits<{ saved: [schedule: Schemas['ScheduleDetail']]; cancel: [] }>()
const { t } = useI18n()
const errorText = useErrorText()
const { busy, run } = useApiAction()

const pipelines = ref<Schemas['PipelineSummary'][]>([])
const pipelineKey = ref(props.schedule?.pipelineKey ?? props.pipelineKey ?? '')
const pipeline = ref<Schemas['PipelineDetail'] | null>(null)
const fields = ref<FieldDefinition[]>([])
const name = ref(props.schedule?.data.name ?? '')
const cron = ref(props.schedule?.data.cron ?? '0 2 * * *')
const timeZone = ref(props.schedule?.data.timeZone ?? 'America/Sao_Paulo')
const pinnedVersion = ref<number | null>(props.schedule?.data.pinnedVersion ?? null)
const values = ref<Record<string, UiValue>>({})
const preview = ref<Schemas['ScheduleOccurrence'][]>([])
const previewError = ref<string | null>(null)
const saveError = ref<string | null>(null)
const form = ref<InstanceType<typeof FieldForm> | null>(null)
const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : ['America/Sao_Paulo', 'UTC']

const hasFileInput = computed(() => fields.value.some((f) => f.type === 'file' || (f.type === 'list' && f.itemType === 'file')))
const versions = computed(() => (pipeline.value?.versions ?? []).filter((v) => v.status === 'Published').map((v) => v.version))

async function loadPipeline(key: string) {
  pipeline.value = null
  fields.value = []
  if (!key) {
    return
  }

  const detail = await run(() => unwrap(api.GET('/pipelines/{key}', { params: { path: { key } } })))
  if (!detail) {
    return
  }

  pipeline.value = detail
  const version = pinnedVersion.value ?? detail.latestPublishedVersion
  if (version) {
    const definition = await run(() =>
      unwrap(api.GET('/pipelines/{key}/versions/{version}', { params: { path: { key, version } } })),
    )
    fields.value = definition?.data.inputs ?? []
    const fixed = (props.schedule?.data.inputs ?? {}) as Record<string, unknown>
    values.value = Object.fromEntries(
      fields.value.filter((f) => f.name in fixed).map((f) => [f.name, fromWire(f, fixed[f.name])]),
    )
  }
}

let previewTimer: ReturnType<typeof setTimeout> | null = null
watch([cron, timeZone], () => {
  if (previewTimer) {
    clearTimeout(previewTimer)
  }

  previewTimer = setTimeout(loadPreview, 300)
})

async function loadPreview() {
  try {
    preview.value = await unwrap(
      api.POST('/schedules/preview', { body: { cron: cron.value, timeZone: timeZone.value, count: 5 } }),
    )
    previewError.value = null
  } catch (error) {
    preview.value = []
    previewError.value = errorText(error)
  }
}

watch(pipelineKey, (key) => void loadPipeline(key))
watch(pinnedVersion, () => void loadPipeline(pipelineKey.value))
onMounted(async () => {
  if (!props.schedule && !props.pipelineKey) {
    const page = await run(() => unwrap(api.GET('/pipelines', { params: { query: { status: 'Published', pageSize: 100 } } })))
    pipelines.value = page?.items ?? []
  }

  await Promise.all([loadPipeline(pipelineKey.value), loadPreview()])
})

async function save() {
  saveError.value = null
  if (!form.value?.check() || hasFileInput.value) {
    return
  }

  const body = {
    name: name.value.trim(),
    cron: cron.value.trim(),
    timeZone: timeZone.value,
    pinnedVersion: pinnedVersion.value,
    inputs: collectInputs(fields.value, values.value).inputs,
    isEnabled: props.schedule?.data.isEnabled ?? true,
  }
  try {
    const saved = props.schedule
      ? await unwrap(
          api.PUT('/schedules/{id}', {
            params: { path: { id: props.schedule.id } },
            body,
            headers: { 'If-Match': `"${props.schedule.rowVersion}"` },
          }),
        )
      : await unwrap(api.POST('/pipelines/{key}/schedules', { params: { path: { key: pipelineKey.value } }, body }))
    emit('saved', saved)
  } catch (error) {
    saveError.value = errorText(error)
  }
}

/** Ocorrência no fuso do agendamento (o texto da API já traz o deslocamento). */
function local(occurrence: Schemas['ScheduleOccurrence']) {
  return occurrence.local.replace('T', ' ').replace(/(\.\d+)?([+-]\d{2}:\d{2}|Z)$/, ' ($2)')
}
</script>

<template>
  <v-card :title="schedule ? t('schedules.edit') : t('schedules.create')" data-testid="schedule-form">
    <v-card-text>
      <v-alert v-if="saveError" type="error" variant="tonal" density="compact" class="mb-3">{{ saveError }}</v-alert>
      <v-select
        v-if="!schedule && !props.pipelineKey"
        v-model="pipelineKey"
        :items="pipelines.map((p) => ({ value: p.key, title: `${p.name} (${p.key})` }))"
        :label="t('schedules.pipeline')"
      />
      <v-text-field v-model="name" :label="t('schedules.name')" counter="200" />
      <v-text-field
        v-model="cron"
        :label="t('schedules.cron')"
        :hint="t('schedules.cronHint')"
        persistent-hint
        data-testid="cron"
      />
      <v-autocomplete v-model="timeZone" :items="zones" :label="t('schedules.timeZone')" class="mt-2" />
      <v-select
        v-model="pinnedVersion"
        :items="[{ value: null, title: t('schedules.latest') }, ...versions.map((v) => ({ value: v, title: `v${v}` }))]"
        :label="t('schedules.version')"
      />
      <div class="text-subtitle-2 mb-1">{{ t('schedules.preview') }}</div>
      <v-alert v-if="previewError" type="error" variant="tonal" density="compact">{{ previewError }}</v-alert>
      <v-list v-else density="compact" class="mb-3 py-0" data-testid="schedule-preview">
        <v-list-item
          v-for="occurrence in preview"
          :key="occurrence.utc"
          :title="local(occurrence)"
          :subtitle="`${formatDateTime(occurrence.utc)} · UTC ${occurrence.utc}`"
        />
      </v-list>
      <v-alert v-if="hasFileInput" type="info" variant="tonal" density="compact" data-testid="schedule-file-input">{{
        t('schedules.fileInput')
      }}</v-alert>
      <template v-else-if="fields.length">
        <div class="text-subtitle-2 mb-2">{{ t('schedules.inputs') }}</div>
        <FieldForm ref="form" v-model="values" :fields="fields" />
      </template>
      <FieldForm v-else ref="form" v-model="values" :fields="[]" class="d-none" />
    </v-card-text>
    <v-card-actions>
      <v-spacer />
      <v-btn variant="text" @click="emit('cancel')">{{ t('common.cancel') }}</v-btn>
      <v-btn
        color="primary"
        :loading="busy"
        :disabled="!pipelineKey || !name.trim() || hasFileInput"
        data-testid="save-schedule"
        @click="save"
      >
        {{ t('common.save') }}
      </v-btn>
    </v-card-actions>
  </v-card>
</template>
