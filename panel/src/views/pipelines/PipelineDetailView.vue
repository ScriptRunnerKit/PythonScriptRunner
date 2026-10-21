<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ApiError, api, apiBase, unwrap, unwrapWithETag, type Schemas } from '@/api/client'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import PageHeader from '@/components/common/PageHeader.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import { autoLayout, buildGraph, readLayout, type StageContract } from '@/components/flow/model'
import { useApiAction, useErrorText } from '@/composables/useApiAction'
import { useContracts } from '@/composables/useContracts'
import { useNotify } from '@/stores/notify'
import { useSession } from '@/stores/session'
import { formatDateTime, formatDuration, elapsedMs } from '@/utils/format'
import ScheduleTable from '@/views/schedules/ScheduleTable.vue'
import RunPipelineDialog from './RunPipelineDialog.vue'

const FlowCanvas = defineAsyncComponent(() => import('@/components/flow/FlowCanvas.vue'))
const DiffView = defineAsyncComponent(() => import('@/components/editor/DiffView.vue'))

/** Trilha (spec 008 §3): versões, diff, agendamentos, configurações, execuções recentes; Disparar (Executor). */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const session = useSession()
const notify = useNotify()
const errorText = useErrorText()
const contracts = useContracts()
const { busy, run } = useApiAction()
const key = computed(() => String(route.params.key))

const pipeline = ref<Schemas['PipelineDetail'] | null>(null)
const etag = ref('')
const tab = ref('versions')
const selected = ref<number | null>(null)
const version = shallowRef<Schemas['PipelineVersionDetail'] | null>(null)
const versionContracts = shallowRef(new Map<string, StageContract>())
const runs = ref<Schemas['RunSummary'][]>([])
const runOpen = ref(false)
const confirmDelete = ref(false)
const diffFrom = ref<number | null>(null)
const diffTo = ref<number | null>(null)
const diffText = ref<[string, string] | null>(null)
const settings = ref({
  maxConcurrentRuns: '' as string | number,
  artifactRetentionDays: '' as string | number,
  runRetentionDays: '' as string | number,
})
const schedules = ref<InstanceType<typeof ScheduleTable> | null>(null)

const draftVersion = computed(() => pipeline.value?.versions.find((v) => v.status === 'Draft')?.version ?? null)
const graph = computed(() => {
  if (!version.value) {
    return null
  }

  const layout = readLayout(version.value.data.layout)
  const first = buildGraph(version.value.data, versionContracts.value, layout)
  return buildGraph(version.value.data, versionContracts.value, autoLayout(first.nodes, first.edges, layout, true))
})

async function load() {
  const result = await run(() => unwrapWithETag(api.GET('/pipelines/{key}', { params: { path: { key: key.value } } })))
  if (!result) {
    return
  }

  pipeline.value = result.data
  etag.value = result.etag
  settings.value = {
    maxConcurrentRuns: result.data.settings.maxConcurrentRuns ?? '',
    artifactRetentionDays: result.data.retention.artifactRetentionDays ?? '',
    runRetentionDays: result.data.retention.runRetentionDays ?? '',
  }
  const versions = result.data.versions
  selected.value ??= result.data.latestPublishedVersion ?? versions[0]?.version ?? null
  diffTo.value ??= versions[0]?.version ?? null
  diffFrom.value ??= versions[1]?.version ?? null
  runs.value =
    (await run(() => unwrap(api.GET('/runs', { params: { query: { pipelineKey: key.value, limit: 20 } } }))))?.items ?? []
}

async function fetchVersion(v: number) {
  return run(() => unwrap(api.GET('/pipelines/{key}/versions/{version}', { params: { path: { key: key.value, version: v } } })))
}

watch(selected, async (v) => {
  version.value = null
  if (!v) {
    return
  }

  const detail = await fetchVersion(v)
  if (detail) {
    versionContracts.value = await contracts.loadAll(detail.data.steps ?? [])
    version.value = detail
  }
})
watch([diffFrom, diffTo, tab], async () => {
  if (tab.value !== 'diff' || !diffFrom.value || !diffTo.value) {
    return
  }

  const [a, b] = await Promise.all([fetchVersion(diffFrom.value), fetchVersion(diffTo.value)])
  const text = (v: typeof a) => JSON.stringify({ ...v?.data, layout: undefined }, null, 2)
  diffText.value = [text(a), text(b)]
})
onMounted(load)

async function edit() {
  if (draftVersion.value === null) {
    const from = pipeline.value?.latestPublishedVersion ?? pipeline.value?.versions[0]?.version
    if (
      !from ||
      !(await run(() =>
        unwrap(api.POST('/pipelines/{key}/draft', { params: { path: { key: key.value } }, body: { fromVersion: from } })),
      ))
    ) {
      return
    }
  }

  await router.push({ name: 'pipeline-editor', params: { key: key.value } })
}

async function archive(v: number) {
  try {
    await unwrap(api.POST('/pipelines/{key}/versions/{version}/archive', { params: { path: { key: key.value, version: v } } }))
    notify.success(t('pipelines.archived', { version: v }))
    await load()
  } catch (error) {
    if (!(error instanceof ApiError && (error.status === 401 || error.status === 403))) {
      notify.error(errorText(error))
    }
  }
}

function number(value: string | number): number | null {
  return value === '' || value === null ? null : Number(value)
}

async function saveSettings() {
  const result = await run(() =>
    unwrapWithETag(
      api.PATCH('/pipelines/{key}/settings', {
        params: { path: { key: key.value } },
        body: { maxConcurrentRuns: number(settings.value.maxConcurrentRuns) },
        headers: { 'If-Match': etag.value },
      }),
    ),
  )
  if (result) {
    notify.success(t('pipelines.settings.saved'))
    await load()
  }
}

async function saveRetention() {
  const result = await run(() =>
    unwrapWithETag(
      api.PATCH('/pipelines/{key}/retention', {
        params: { path: { key: key.value } },
        body: {
          artifactRetentionDays: number(settings.value.artifactRetentionDays),
          runRetentionDays: number(settings.value.runRetentionDays),
        },
        headers: { 'If-Match': etag.value },
      }),
    ),
  )
  if (result) {
    notify.success(t('pipelines.settings.saved'))
    await load()
  }
}

async function remove() {
  const done = await run(async () => {
    await unwrap(api.DELETE('/pipelines/{key}', { params: { path: { key: key.value } } }))
    return true
  })
  confirmDelete.value = false
  if (done) {
    await router.push({ name: 'pipelines' })
  }
}

const exportUrl = (v: number) => `${apiBase}/pipelines/${encodeURIComponent(key.value)}/versions/${v}/export`
</script>

<template>
  <v-container fluid>
    <template v-if="pipeline">
      <PageHeader :title="pipeline.name" :subtitle="`${pipeline.key}${pipeline.description ? ' · ' + pipeline.description : ''}`">
        <v-btn
          v-if="session.hasRole('Executor') && pipeline.latestPublishedVersion"
          color="primary"
          prepend-icon="mdi-play"
          data-testid="run-pipeline"
          @click="runOpen = true"
        >
          {{ t('pipelines.run.open') }}
        </v-btn>
        <v-btn
          v-if="session.hasRole('Author')"
          variant="tonal"
          prepend-icon="mdi-pencil"
          data-testid="edit-pipeline"
          @click="edit"
        >
          {{ draftVersion ? t('stages.editDraft', { version: draftVersion }) : t('stages.newVersion') }}
        </v-btn>
        <v-btn
          v-if="session.hasRole('Admin')"
          variant="text"
          color="error"
          prepend-icon="mdi-delete-outline"
          data-testid="delete-pipeline"
          @click="confirmDelete = true"
        >
          {{ t('common.delete') }}
        </v-btn>
      </PageHeader>

      <v-card>
        <v-tabs v-model="tab" show-arrows>
          <v-tab value="versions">{{ t('stages.tabs.versions') }}</v-tab>
          <v-tab value="diff" :disabled="pipeline.versions.length < 2">{{ t('stages.tabs.diff') }}</v-tab>
          <v-tab value="schedules">{{ t('nav.schedules') }}</v-tab>
          <v-tab value="runs">{{ t('pipelines.recentRuns') }}</v-tab>
          <v-tab value="settings">{{ t('pipelines.settings.title') }}</v-tab>
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
                <tr
                  v-for="v in pipeline.versions"
                  :key="v.version"
                  :data-version="v.version"
                  :class="{ 'bg-surface-light': v.version === selected }"
                >
                  <td>v{{ v.version }}</td>
                  <td><StatusChip :status="v.status" kind="version" /></td>
                  <td>{{ formatDateTime(v.publishedAt) }}</td>
                  <td>{{ formatDateTime(v.updatedAt) }}</td>
                  <td class="text-no-wrap text-right">
                    <v-btn size="small" variant="text" @click="selected = v.version">{{ t('common.view') }}</v-btn>
                    <v-btn
                      v-if="v.status !== 'Draft'"
                      size="small"
                      variant="text"
                      :href="exportUrl(v.version)"
                      download
                      prepend-icon="mdi-download"
                      >{{ t('catalog.export') }}</v-btn
                    >
                    <v-btn
                      v-if="v.status === 'Published' && session.hasRole('Author')"
                      size="small"
                      variant="text"
                      @click="archive(v.version)"
                      >{{ t('catalog.archive') }}</v-btn
                    >
                  </td>
                </tr>
              </tbody>
            </v-table>
            <div v-if="graph" class="pa-4">
              <FlowCanvas :nodes="graph.nodes" :edges="graph.edges" :label="t('pipelines.canvas')" height="420px" />
            </div>
          </v-window-item>
          <v-window-item value="diff">
            <div class="pa-4">
              <div class="d-flex ga-3 mb-3">
                <v-select
                  v-model="diffFrom"
                  :items="pipeline.versions.map((v) => ({ value: v.version, title: `v${v.version}` }))"
                  :label="t('stages.diffFrom')"
                  hide-details
                  style="max-width: 160px"
                />
                <v-select
                  v-model="diffTo"
                  :items="pipeline.versions.map((v) => ({ value: v.version, title: `v${v.version}` }))"
                  :label="t('stages.diffTo')"
                  hide-details
                  style="max-width: 160px"
                />
              </div>
              <DiffView v-if="diffText" :original="diffText[0]" :modified="diffText[1]" language="json" />
            </div>
          </v-window-item>
          <v-window-item value="schedules">
            <div class="pa-2">
              <div v-if="session.hasRole('Author')" class="d-flex pa-2">
                <v-spacer />
                <v-btn color="primary" prepend-icon="mdi-plus" data-testid="create-schedule" @click="schedules?.create()">{{
                  t('schedules.create')
                }}</v-btn>
              </div>
              <ScheduleTable ref="schedules" :pipeline-key="pipeline.key" />
            </div>
          </v-window-item>
          <v-window-item value="runs">
            <v-list density="compact">
              <v-list-item
                v-for="item in runs"
                :key="item.id"
                :to="{ name: 'run', params: { id: item.id } }"
                :title="`${formatDateTime(item.createdAt)} · v${item.targetVersion}`"
                :subtitle="`${t(`trigger.${item.trigger}`)} · ${formatDuration(elapsedMs(item.startedAt, item.finishedAt))}`"
              >
                <template #append><StatusChip :status="item.status" /></template>
              </v-list-item>
              <v-list-item v-if="runs.length === 0" :title="t('runs.empty')" />
            </v-list>
            <div class="pa-2">
              <v-btn variant="text" :to="{ name: 'runs', query: { pipelineKey: pipeline.key } }">{{
                t('pipelines.allRuns')
              }}</v-btn>
            </div>
          </v-window-item>
          <v-window-item value="settings">
            <div class="pa-4" style="max-width: 560px">
              <div class="text-subtitle-2 mb-2">{{ t('pipelines.settings.concurrency') }}</div>
              <v-text-field
                v-model="settings.maxConcurrentRuns"
                type="number"
                min="1"
                :label="t('pipelines.settings.maxConcurrentRuns')"
                :hint="t('pipelines.settings.maxConcurrentRunsHint')"
                persistent-hint
                :readonly="!session.hasRole('Author')"
              />
              <v-btn v-if="session.hasRole('Author')" class="mt-2" variant="tonal" :loading="busy" @click="saveSettings">{{
                t('common.save')
              }}</v-btn>
              <div class="text-subtitle-2 mt-6 mb-2">{{ t('pipelines.settings.retention') }}</div>
              <v-row density="compact">
                <v-col cols="6">
                  <v-text-field
                    v-model="settings.artifactRetentionDays"
                    type="number"
                    min="1"
                    :label="t('pipelines.settings.artifactDays')"
                    :readonly="!session.hasRole('Admin')"
                    suffix="d"
                  />
                </v-col>
                <v-col cols="6">
                  <v-text-field
                    v-model="settings.runRetentionDays"
                    type="number"
                    min="1"
                    :label="t('pipelines.settings.runDays')"
                    :readonly="!session.hasRole('Admin')"
                    suffix="d"
                  />
                </v-col>
              </v-row>
              <p class="text-caption text-medium-emphasis">{{ t('pipelines.settings.retentionHint') }}</p>
              <v-btn v-if="session.hasRole('Admin')" class="mt-2" variant="tonal" :loading="busy" @click="saveRetention">{{
                t('common.save')
              }}</v-btn>
            </div>
          </v-window-item>
        </v-window>
      </v-card>

      <RunPipelineDialog v-if="pipeline.latestPublishedVersion" v-model="runOpen" :pipeline="pipeline" />
      <ConfirmDialog
        v-model="confirmDelete"
        :title="t('pipelines.deleteTitle')"
        :text="t('pipelines.deleteText', { key: pipeline.key })"
        :confirm="t('common.delete')"
        color="error"
        :busy="busy"
        @confirm="remove"
      />
    </template>
    <v-skeleton-loader v-else type="article, table" />
  </v-container>
</template>
