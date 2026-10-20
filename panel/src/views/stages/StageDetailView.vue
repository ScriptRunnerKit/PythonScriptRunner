<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ApiError, api, apiBase, unwrap, type Schemas } from '@/api/client'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import { typeLabel } from '@/components/flow/model'
import { useApiAction, useErrorText } from '@/composables/useApiAction'
import { useNotify } from '@/stores/notify'
import { useSession } from '@/stores/session'
import { formatDateTime } from '@/utils/format'

const CodeEditor = defineAsyncComponent(() => import('@/components/editor/CodeEditor.vue'))
const DiffView = defineAsyncComponent(() => import('@/components/editor/DiffView.vue'))

/** Etapa (spec 008 §2): versões, diff entre versões, trilhas que usam cada versão, exportar zip, arquivar e excluir. */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const session = useSession()
const notify = useNotify()
const errorText = useErrorText()
const { busy, run } = useApiAction()
const key = computed(() => String(route.params.key))

const stage = ref<Schemas['StageDetail'] | null>(null)
const tab = ref('versions')
const selected = ref<number | null>(null)
const version = ref<Schemas['StageVersionDetail'] | null>(null)
const diffFrom = ref<number | null>(null)
const diffTo = ref<number | null>(null)
const diffA = ref<Schemas['StageVersionDetail'] | null>(null)
const diffB = ref<Schemas['StageVersionDetail'] | null>(null)
const confirmDelete = ref(false)
const archiveBlocked = ref<string[] | null>(null)

const draftVersion = computed(() => stage.value?.versions.find((v) => v.status === 'Draft')?.version ?? null)

async function load() {
  stage.value = (await run(() => unwrap(api.GET('/stages/{key}', { params: { path: { key: key.value } } })))) ?? null
  const versions = stage.value?.versions ?? []
  selected.value ??= stage.value?.latestPublishedVersion ?? versions[0]?.version ?? null
  diffTo.value ??= versions[0]?.version ?? null
  diffFrom.value ??= versions[1]?.version ?? null
}

async function fetchVersion(v: number) {
  return run(() => unwrap(api.GET('/stages/{key}/versions/{version}', { params: { path: { key: key.value, version: v } } })))
}

watch(selected, async (v) => (version.value = v ? ((await fetchVersion(v)) ?? null) : null))
watch([diffFrom, diffTo, tab], async () => {
  if (tab.value !== 'diff' || !diffFrom.value || !diffTo.value) {
    return
  }

  ;[diffA.value, diffB.value] = await Promise.all([fetchVersion(diffFrom.value), fetchVersion(diffTo.value)]).then((r) => [
    r[0] ?? null,
    r[1] ?? null,
  ])
})
onMounted(load)

async function edit() {
  if (draftVersion.value === null) {
    const from = stage.value?.latestPublishedVersion ?? stage.value?.versions[0]?.version
    if (!from) {
      return
    }

    const created = await run(() =>
      unwrap(api.POST('/stages/{key}/draft', { params: { path: { key: key.value } }, body: { fromVersion: from } })),
    )
    if (!created) {
      return
    }
  }

  await router.push({ name: 'stage-editor', params: { key: key.value } })
}

async function archive(v: number) {
  archiveBlocked.value = null
  try {
    await unwrap(api.POST('/stages/{key}/versions/{version}/archive', { params: { path: { key: key.value, version: v } } }))
    notify.success(t('stages.archived', { version: v }))
    await load()
  } catch (error) {
    if (error instanceof ApiError && error.code === 'VERSION_REFERENCED') {
      archiveBlocked.value = error.extension<string[]>('pipelines') ?? []
    } else if (!(error instanceof ApiError && (error.status === 401 || error.status === 403))) {
      notify.error(errorText(error))
    }
  }
}

async function remove() {
  const done = await run(async () => {
    await unwrap(api.DELETE('/stages/{key}', { params: { path: { key: key.value } } }))
    return true
  })
  confirmDelete.value = false
  if (done) {
    notify.success(t('stages.deleted'))
    await router.push({ name: 'stages' })
  }
}

const exportUrl = (v: number) => `${apiBase}/stages/${encodeURIComponent(key.value)}/versions/${v}/export`
</script>

<template>
  <v-container fluid>
    <template v-if="stage">
      <PageHeader :title="stage.name" :subtitle="`${stage.key}${stage.description ? ' · ' + stage.description : ''}`">
        <v-btn v-if="session.hasRole('Author')" color="primary" prepend-icon="mdi-pencil" data-testid="edit-stage" @click="edit">
          {{ draftVersion ? t('stages.editDraft', { version: draftVersion }) : t('stages.newVersion') }}
        </v-btn>
        <v-btn
          v-if="session.hasRole('Admin')"
          color="error"
          variant="text"
          prepend-icon="mdi-delete-outline"
          data-testid="delete-stage"
          @click="confirmDelete = true"
        >
          {{ t('common.delete') }}
        </v-btn>
      </PageHeader>

      <v-alert
        v-if="archiveBlocked"
        type="warning"
        variant="tonal"
        closable
        class="mb-4"
        data-testid="archive-blocked"
        @click:close="archiveBlocked = null"
      >
        {{ t('stages.archiveBlocked', { pipelines: archiveBlocked.join(', ') }) }}
      </v-alert>

      <v-card>
        <v-tabs v-model="tab">
          <v-tab value="versions">{{ t('stages.tabs.versions') }}</v-tab>
          <v-tab value="version">{{ t('stages.tabs.version') }}</v-tab>
          <v-tab value="usage">{{ t('stages.tabs.usage') }}</v-tab>
          <v-tab value="diff" :disabled="stage.versions.length < 2">{{ t('stages.tabs.diff') }}</v-tab>
        </v-tabs>
        <v-divider />
        <v-window v-model="tab">
          <v-window-item value="versions">
            <v-table density="comfortable" data-testid="versions-table">
              <thead>
                <tr>
                  <th>{{ t('catalog.version') }}</th>
                  <th>{{ t('catalog.status') }}</th>
                  <th>{{ t('catalog.publishedAt') }}</th>
                  <th>{{ t('catalog.updated') }}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                <tr v-for="v in stage.versions" :key="v.version" :data-version="v.version">
                  <td>v{{ v.version }}</td>
                  <td><StatusChip :status="v.status" kind="version" /></td>
                  <td>{{ formatDateTime(v.publishedAt) }}</td>
                  <td>{{ formatDateTime(v.updatedAt) }}</td>
                  <td class="text-no-wrap text-right">
                    <v-btn size="small" variant="text" @click="((selected = v.version), (tab = 'version'))">{{
                      t('common.view')
                    }}</v-btn>
                    <v-btn
                      v-if="v.status !== 'Draft'"
                      size="small"
                      variant="text"
                      :href="exportUrl(v.version)"
                      download
                      prepend-icon="mdi-download"
                    >
                      {{ t('catalog.export') }}
                    </v-btn>
                    <v-btn
                      v-if="v.status === 'Published' && session.hasRole('Author')"
                      size="small"
                      variant="text"
                      :loading="busy"
                      @click="archive(v.version)"
                    >
                      {{ t('catalog.archive') }}
                    </v-btn>
                  </td>
                </tr>
              </tbody>
            </v-table>
          </v-window-item>
          <v-window-item value="version">
            <div v-if="version" class="pa-4">
              <div class="d-flex align-center ga-2 mb-3">
                <v-select
                  v-model="selected"
                  :items="stage.versions.map((v) => ({ value: v.version, title: `v${v.version}` }))"
                  hide-details
                  style="max-width: 160px"
                />
                <StatusChip :status="version.status" kind="version" />
                <span v-if="version.publishedBy" class="text-caption text-medium-emphasis">{{
                  t('stages.publishedBy', {
                    who: version.publishedBy,
                    when: formatDateTime(version.publishedAt),
                  })
                }}</span>
              </div>
              <CodeEditor
                :model-value="version.data.script"
                language="python"
                readonly
                height="320px"
                :label="t('stages.script')"
              />
              <v-row class="mt-2">
                <v-col cols="12" md="6">
                  <div class="text-subtitle-2">{{ t('stages.inputs') }}</div>
                  <v-chip v-for="field in version.data.inputs ?? []" :key="field.name" size="small" class="mr-1 mt-1" label
                    >{{ field.name }}: {{ typeLabel(field) }}</v-chip
                  >
                </v-col>
                <v-col cols="12" md="6">
                  <div class="text-subtitle-2">{{ t('stages.outputs') }}</div>
                  <v-chip v-for="field in version.data.outputs ?? []" :key="field.name" size="small" class="mr-1 mt-1" label
                    >{{ field.name }}: {{ typeLabel(field) }}</v-chip
                  >
                </v-col>
              </v-row>
              <div v-if="version.lockedRequirements" class="mt-3">
                <div class="text-subtitle-2">{{ t('stages.lock') }}</div>
                <pre class="stage-detail__lock">{{ version.lockedRequirements }}</pre>
              </div>
            </div>
          </v-window-item>
          <v-window-item value="usage">
            <v-table density="comfortable" data-testid="usage-table">
              <thead>
                <tr>
                  <th>{{ t('catalog.pipeline') }}</th>
                  <th>{{ t('catalog.status') }}</th>
                  <th>{{ t('stages.step') }}</th>
                  <th>{{ t('catalog.version') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(usage, i) in stage.usedBy" :key="i">
                  <td>
                    <router-link :to="{ name: 'pipeline', params: { key: usage.pipelineKey } }"
                      >{{ usage.pipelineKey }}@{{ usage.pipelineVersion }}</router-link
                    >
                  </td>
                  <td><StatusChip :status="usage.pipelineStatus" kind="version" /></td>
                  <td>{{ usage.stepKey }}</td>
                  <td>v{{ usage.stageVersion }}</td>
                </tr>
                <tr v-if="stage.usedBy.length === 0">
                  <td colspan="4" class="text-center text-medium-emphasis py-4">{{ t('stages.unused') }}</td>
                </tr>
              </tbody>
            </v-table>
          </v-window-item>
          <v-window-item value="diff">
            <div class="pa-4">
              <div class="d-flex ga-3 mb-3">
                <v-select
                  v-model="diffFrom"
                  :items="stage.versions.map((v) => ({ value: v.version, title: `v${v.version}` }))"
                  :label="t('stages.diffFrom')"
                  hide-details
                  style="max-width: 160px"
                />
                <v-select
                  v-model="diffTo"
                  :items="stage.versions.map((v) => ({ value: v.version, title: `v${v.version}` }))"
                  :label="t('stages.diffTo')"
                  hide-details
                  style="max-width: 160px"
                />
              </div>
              <DiffView v-if="diffA && diffB" :original="diffA.data.script" :modified="diffB.data.script" />
              <div v-if="diffA && diffB" class="mt-3">
                <div class="text-subtitle-2 mb-1">{{ t('stages.requirements') }}</div>
                <DiffView
                  :original="diffA.data.requirements ?? ''"
                  :modified="diffB.data.requirements ?? ''"
                  language="plaintext"
                  height="160px"
                />
              </div>
            </div>
          </v-window-item>
        </v-window>
      </v-card>
      <ConfirmDialog
        v-model="confirmDelete"
        :title="t('stages.deleteTitle')"
        :text="t('stages.deleteText', { key: stage.key })"
        :confirm="t('common.delete')"
        color="error"
        :busy="busy"
        @confirm="remove"
      />
    </template>
    <v-skeleton-loader v-else type="article, table" />
  </v-container>
</template>

<style scoped>
.stage-detail__lock {
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 12px;
  max-height: 240px;
  overflow: auto;
  background: rgba(var(--v-theme-on-surface), 0.04);
  padding: 8px;
  border-radius: 4px;
}
</style>
