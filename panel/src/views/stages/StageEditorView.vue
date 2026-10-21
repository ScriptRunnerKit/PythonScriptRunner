<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ApiError, api, unwrapWithETag, type Schemas } from '@/api/client'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import ConflictDialog from '@/components/common/ConflictDialog.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import ProblemList from '@/components/common/ProblemList.vue'
import type { Marker } from '@/components/editor/types'
import FieldBuilder from '@/components/fields/FieldBuilder.vue'
import type { FieldDefinition } from '@/components/fields/values'
import { useErrorText } from '@/composables/useApiAction'
import { useLocalDraft } from '@/composables/useLocalDraft'
import { useNotify } from '@/stores/notify'
import { formatDateTime, timeSpanMs, toTimeSpan } from '@/utils/format'
import StageTestPanel from './StageTestPanel.vue'

const CodeEditor = defineAsyncComponent(() => import('@/components/editor/CodeEditor.vue'))

type Data = Schemas['StageDraftData']
type Draft = Schemas['StageDraft']

/**
 * Editor de Etapa (spec 008 §2): Monaco com Python, construtor de campos, dependências, execução, salvar com ETag
 * (conflito → Recarregar/Sobrescrever; D-50), validar, testar pela fila (D-114) e publicar (D-141).
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const notify = useNotify()
const errorText = useErrorText()
const key = computed(() => String(route.params.key))

const draft = ref<Draft | null>(null)
const etag = ref('')
const form = ref<Data | null>(null)
const noDraft = ref(false)
const panel = ref('contract')
const saving = ref(false)
const publishing = ref(false)
const report = ref<Schemas['ValidationReport'] | null>(null)
const markers = ref<Marker[]>([])
const publishError = ref<string | null>(null)
const published = ref<Schemas['StageVersionDetail'] | null>(null)
const conflict = ref<Draft | null>(null)
const confirmDiscard = ref(false)
const pythonVersions = ['3.10', '3.11', '3.12', '3.13']

const dirty = computed(
  () => form.value !== null && draft.value !== null && JSON.stringify(form.value) !== JSON.stringify(draft.value.data),
)
const local = useLocalDraft<Data>(
  () => `stage:${key.value}`,
  () => (dirty.value ? form.value! : undefined),
)

function take(result: { data: Draft; etag: string }) {
  draft.value = result.data
  etag.value = result.etag || `"${result.data.rowVersion}"`
  form.value = structuredClone(result.data.data)
}

async function load() {
  try {
    take(await unwrapWithETag(api.GET('/stages/{key}/draft', { params: { path: { key: key.value } } })))
    noDraft.value = false
    local.check()
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      noDraft.value = true
    } else {
      notify.error(errorText(error))
    }
  }
}

onMounted(load)

function restore() {
  if (local.offer.value) {
    form.value = structuredClone(local.offer.value.data)
    local.offer.value = null
  }
}

/** Salva o rascunho (If-Match); devolve `false` quando não salvou (conflito ou erro). */
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
        api.PUT('/stages/{key}/draft', {
          params: { path: { key: key.value } },
          body: form.value,
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

async function validate() {
  if (!(await save())) {
    return
  }

  try {
    report.value = (await unwrapWithETag(api.POST('/stages/{key}/draft/validate', { params: { path: { key: key.value } } }))).data
    panel.value = 'problems'
  } catch (error) {
    notify.error(errorText(error))
  }
}

async function publish() {
  publishError.value = null
  markers.value = []
  if (!(await save())) {
    return
  }

  publishing.value = true
  try {
    const result = await unwrapWithETag(
      api.POST('/stages/{key}/draft/publish', {
        params: { path: { key: key.value } },
        headers: { 'If-Match': etag.value },
      }),
    )
    published.value = result.data
    local.saved()
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.code === 'SCRIPT_SYNTAX_ERROR') {
        const line = error.extension<number>('line')
        const column = error.extension<number>('column')
        publishError.value = t('stages.publish.syntax', {
          line: line ?? '?',
          column: column ?? '?',
          message: error.message,
        })
        if (line) {
          markers.value = [{ line, column: column ?? 1, message: error.message }]
        }
      } else if (error.code === 'STAGE_INVALID') {
        report.value = error.extension<Schemas['ValidationReport']>('report') ?? null
        panel.value = 'problems'
        publishError.value = t('stages.publish.invalid')
      } else if (error.code === 'CONCURRENCY_CONFLICT') {
        conflict.value = error.extension<Draft>('current') ?? null
      } else if (error.status !== 401 && error.status !== 403) {
        publishError.value = errorText(error)
      }
    } else {
      publishError.value = errorText(error)
    }
  } finally {
    publishing.value = false
  }
}

async function createDraft() {
  try {
    const stage = await unwrapWithETag(api.GET('/stages/{key}', { params: { path: { key: key.value } } }))
    const from = stage.data.latestPublishedVersion ?? stage.data.versions[0]?.version
    if (from) {
      await unwrapWithETag(api.POST('/stages/{key}/draft', { params: { path: { key: key.value } }, body: { fromVersion: from } }))
      await load()
    }
  } catch (error) {
    notify.error(errorText(error))
  }
}

async function discard() {
  try {
    await unwrapWithETag(
      api.DELETE('/stages/{key}/draft', { params: { path: { key: key.value } }, headers: { 'If-Match': etag.value } }),
    )
    local.saved()
    form.value = null
    draft.value = null
    await router.push({ name: 'stage', params: { key: key.value } })
  } catch (error) {
    notify.error(errorText(error))
  } finally {
    confirmDiscard.value = false
  }
}

function finishPublish() {
  form.value = null
  draft.value = null
  void router.push({ name: 'stage', params: { key: key.value } })
}

const timeoutSeconds = computed({
  get: () => Math.round((timeSpanMs(form.value?.timeout) ?? 300_000) / 1000),
  set: (value: number) => form.value && (form.value.timeout = toTimeSpan(Math.max(1, value) * 1000)),
})
const retryDelaySeconds = computed({
  get: () => Math.round((timeSpanMs(form.value?.retry?.initialDelay) ?? 0) / 1000),
  set: (value: number) => form.value && (form.value.retry = { ...retry(), initialDelay: toTimeSpan(Math.max(0, value) * 1000) }),
})

function retry(): Schemas['RetryPolicy'] {
  return form.value?.retry ?? { maxAttempts: 0, initialDelay: '00:00:00', backoffMultiplier: 2 }
}
</script>

<template>
  <v-container fluid>
    <PageHeader
      :title="t('stages.editor.title')"
      :subtitle="draft ? t('stages.editor.subtitle', { key, version: draft.version }) : key"
    >
      <template v-if="form">
        <v-chip v-if="dirty" color="warning" size="small" label data-testid="dirty">{{ t('editor.unsaved') }}</v-chip>
        <v-btn variant="tonal" prepend-icon="mdi-content-save" :loading="saving" data-testid="save" @click="save()">{{
          t('common.save')
        }}</v-btn>
        <v-btn variant="text" prepend-icon="mdi-check-decagram-outline" data-testid="validate" @click="validate">{{
          t('editor.validate')
        }}</v-btn>
        <v-btn variant="text" prepend-icon="mdi-play-circle-outline" data-testid="open-test" @click="panel = 'test'">{{
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

    <v-alert v-if="noDraft" type="info" variant="tonal" class="mb-4">
      {{ t('stages.editor.noDraft') }}
      <template #append
        ><v-btn color="primary" @click="createDraft">{{ t('stages.newVersion') }}</v-btn></template
      >
    </v-alert>
    <v-alert v-if="local.offer.value" type="info" variant="tonal" class="mb-4" data-testid="restore-offer">
      {{ t('drafts.offer', { when: formatDateTime(local.offer.value.savedAt) }) }}
      <template #append>
        <v-btn variant="text" data-testid="restore" @click="restore">{{ t('drafts.restore') }}</v-btn>
        <v-btn variant="text" @click="local.discard()">{{ t('drafts.discard') }}</v-btn>
      </template>
    </v-alert>
    <v-alert v-if="publishing" type="info" variant="tonal" class="mb-4" data-testid="publishing">
      <v-progress-circular indeterminate size="18" width="2" class="mr-2" />{{ t('stages.publish.progress') }}
    </v-alert>
    <v-alert
      v-if="publishError"
      type="error"
      variant="tonal"
      class="mb-4"
      data-testid="publish-error"
      closable
      @click:close="publishError = null"
    >
      {{ publishError }}
    </v-alert>

    <v-row v-if="form">
      <v-col cols="12" lg="7">
        <CodeEditor
          v-model="form.script"
          language="python"
          height="calc(100vh - 260px)"
          :markers="markers"
          :label="t('stages.script')"
          @save="save()"
        />
      </v-col>
      <v-col cols="12" lg="5">
        <v-card>
          <v-tabs v-model="panel" show-arrows density="comfortable">
            <v-tab value="contract">{{ t('stages.editor.contract') }}</v-tab>
            <v-tab value="dependencies">{{ t('stages.editor.dependencies') }}</v-tab>
            <v-tab value="execution">{{ t('stages.editor.execution') }}</v-tab>
            <v-tab value="problems">{{ t('stages.editor.problems') }}</v-tab>
            <v-tab value="test">{{ t('editor.test') }}</v-tab>
          </v-tabs>
          <v-divider />
          <v-window v-model="panel" class="pa-4">
            <v-window-item value="contract">
              <div class="text-subtitle-2 mb-2">{{ t('stages.inputs') }}</div>
              <FieldBuilder
                :model-value="form.inputs ?? []"
                kind="inputs"
                @update:model-value="(v: FieldDefinition[]) => form && (form.inputs = v)"
              />
              <div class="text-subtitle-2 mt-6 mb-2">{{ t('stages.outputs') }}</div>
              <FieldBuilder
                :model-value="form.outputs ?? []"
                kind="outputs"
                @update:model-value="(v: FieldDefinition[]) => form && (form.outputs = v)"
              />
            </v-window-item>
            <v-window-item value="dependencies">
              <v-select
                v-model="form.pythonVersion"
                :items="[...new Set([...pythonVersions, form.pythonVersion])]"
                :label="t('stages.editor.python')"
              />
              <div class="text-body-2 mb-1">{{ t('stages.requirements') }}</div>
              <CodeEditor
                :model-value="form.requirements ?? ''"
                language="plaintext"
                height="220px"
                :label="t('stages.requirements')"
                @update:model-value="(v: string) => form && (form.requirements = v || null)"
              />
              <p class="text-caption text-medium-emphasis mt-2">{{ t('stages.editor.requirementsHint') }}</p>
            </v-window-item>
            <v-window-item value="execution">
              <v-text-field
                v-model.number="timeoutSeconds"
                type="number"
                min="1"
                :label="t('stages.editor.timeout')"
                suffix="s"
              />
              <v-text-field
                :model-value="form.memoryLimitMb ?? ''"
                type="number"
                min="64"
                :label="t('stages.editor.memory')"
                :hint="t('stages.editor.memoryHint')"
                persistent-hint
                suffix="MB"
                @update:model-value="(v: string) => form && (form.memoryLimitMb = v === '' ? null : Number(v))"
              />
              <div class="text-subtitle-2 mt-4 mb-2">{{ t('stages.editor.retry') }}</div>
              <v-row density="compact">
                <v-col cols="4">
                  <v-text-field
                    :model-value="retry().maxAttempts"
                    type="number"
                    min="0"
                    :label="t('stages.editor.retries')"
                    @update:model-value="(v: string) => form && (form.retry = { ...retry(), maxAttempts: Number(v) })"
                  />
                </v-col>
                <v-col cols="4"
                  ><v-text-field
                    v-model.number="retryDelaySeconds"
                    type="number"
                    min="0"
                    :label="t('stages.editor.retryDelay')"
                    suffix="s"
                /></v-col>
                <v-col cols="4">
                  <v-text-field
                    :model-value="retry().backoffMultiplier"
                    type="number"
                    min="1"
                    step="0.5"
                    :label="t('stages.editor.backoff')"
                    @update:model-value="(v: string) => form && (form.retry = { ...retry(), backoffMultiplier: Number(v) })"
                  />
                </v-col>
              </v-row>
              <v-switch
                v-model="form.idempotent"
                :label="t('stages.editor.idempotent')"
                :hint="t('stages.editor.idempotentHint')"
                persistent-hint
                color="primary"
                inset
              />
              <v-combobox
                v-model="form.requiredSecrets"
                :label="t('stages.editor.secrets')"
                :hint="t('stages.editor.secretsHint')"
                persistent-hint
                multiple
                chips
                closable-chips
                class="mt-4"
              />
            </v-window-item>
            <v-window-item value="problems">
              <ProblemList :report="report" />
              <p v-if="!report" class="text-medium-emphasis">{{ t('stages.editor.validateHint') }}</p>
            </v-window-item>
            <v-window-item value="test">
              <StageTestPanel
                :stage-key="key"
                :inputs="form.inputs ?? []"
                :outputs="form.outputs ?? []"
                :before-run="() => save()"
              />
            </v-window-item>
          </v-window>
        </v-card>
      </v-col>
    </v-row>

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
    <v-dialog
      :model-value="published !== null"
      max-width="640"
      persistent
      :aria-label="published ? t('stages.publish.done', { version: published.version }) : undefined"
    >
      <v-card v-if="published" :title="t('stages.publish.done', { version: published.version })" data-testid="published">
        <v-card-text>
          <div class="text-subtitle-2 mb-1">{{ t('stages.lock') }}</div>
          <pre class="stage-editor__lock">{{ published.lockedRequirements || t('stages.publish.noDependencies') }}</pre>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn color="primary" @click="finishPublish">{{ t('stages.publish.view') }}</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </v-container>
</template>

<style scoped>
.stage-editor__lock {
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 12px;
  max-height: 280px;
  overflow: auto;
  background: rgba(var(--v-theme-on-surface), 0.04);
  padding: 8px;
  border-radius: 4px;
}
</style>
