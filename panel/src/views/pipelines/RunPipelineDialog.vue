<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { api, apiFetch, ensureOk, unwrap, type Schemas } from '@/api/client'
import FieldForm from '@/components/fields/FieldForm.vue'
import { collectInputs, toFormData, type FieldDefinition, type UiValue } from '@/components/fields/values'
import { useErrorText } from '@/composables/useApiAction'

/** Disparar uma trilha (Executor; spec 008 §3): formulário gerado, versão (última/fixa), chave de idempotência opcional. */
const props = defineProps<{ pipeline: Schemas['PipelineDetail'] }>()
const open = defineModel<boolean>({ required: true })
const { t } = useI18n()
const router = useRouter()
const errorText = useErrorText()

const version = ref<number | null>(null)
const idempotencyKey = ref('')
const fields = ref<FieldDefinition[]>([])
const values = ref<Record<string, UiValue>>({})
const form = ref<InstanceType<typeof FieldForm> | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)
const published = computed(() => props.pipeline.versions.filter((v) => v.status === 'Published').map((v) => v.version))

async function loadFields() {
  const v = version.value ?? props.pipeline.latestPublishedVersion
  fields.value = []
  if (!v) {
    return
  }

  const detail = await unwrap(
    api.GET('/pipelines/{key}/versions/{version}', { params: { path: { key: props.pipeline.key, version: v } } }),
  )
  fields.value = detail.data.inputs ?? []
}

watch([open, version], ([isOpen]) => isOpen && void loadFields(), { immediate: true })

async function submit() {
  error.value = null
  if (!form.value?.check()) {
    return
  }

  busy.value = true
  try {
    const body = toFormData(collectInputs(fields.value, values.value), {
      version: version.value === null ? undefined : String(version.value),
      idempotencyKey: idempotencyKey.value.trim() || undefined,
    })
    const response = await ensureOk(
      await apiFetch(`/pipelines/${encodeURIComponent(props.pipeline.key)}/runs`, { method: 'POST', body }),
    )
    const handle = (await response.json()) as Schemas['RunHandle']
    open.value = false
    await router.push({ name: 'run', params: { id: handle.runId } })
  } catch (e) {
    error.value = errorText(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <v-dialog v-model="open" max-width="720" scrollable :aria-label="t('pipelines.run.title', { name: pipeline.name })">
    <v-card :title="t('pipelines.run.title', { name: pipeline.name })" data-testid="run-dialog">
      <v-card-text>
        <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mb-3">{{ error }}</v-alert>
        <v-row density="compact">
          <v-col cols="12" md="6">
            <v-select
              v-model="version"
              :items="[
                { value: null, title: t('pipelines.run.latest', { version: pipeline.latestPublishedVersion }) },
                ...published.map((v) => ({ value: v, title: `v${v}` })),
              ]"
              :label="t('pipelines.run.version')"
            />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field
              v-model="idempotencyKey"
              :label="t('pipelines.run.idempotencyKey')"
              :hint="t('pipelines.run.idempotencyHint')"
              persistent-hint
            />
          </v-col>
        </v-row>
        <div class="text-subtitle-2 mt-2 mb-2">{{ t('pipelines.run.inputs') }}</div>
        <FieldForm ref="form" v-model="values" :fields="fields" />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="open = false">{{ t('common.cancel') }}</v-btn>
        <v-btn color="primary" prepend-icon="mdi-play" :loading="busy" data-testid="submit-run" @click="submit">{{
          t('pipelines.run.submit')
        }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
