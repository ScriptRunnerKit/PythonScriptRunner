<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { api, apiFetch, ensureOk, unwrap, type Schemas } from '@/api/client'
import PageHeader from '@/components/common/PageHeader.vue'
import { useApiAction, useErrorText } from '@/composables/useApiAction'
import { useNotify } from '@/stores/notify'
import { useSession } from '@/stores/session'
import { formatDateTime } from '@/utils/format'

type Summary = Schemas['StageSummary'] | Schemas['PipelineSummary']

/** Lista de Etapas ou Trilhas (spec 008): busca, filtro por status (e por trilha, nas Etapas), versão publicada, uso; Autor cria e importa. */
const props = defineProps<{ kind: 'stages' | 'pipelines' }>()
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const session = useSession()
const notify = useNotify()
const errorText = useErrorText()
const { busy, run } = useApiAction()

const search = ref('')
const status = ref<Schemas['VersionStatus'] | null>(null)
const page = ref(1)
const pageSize = ref(25)
const items = ref<Summary[]>([])
const total = ref(0)
const creating = ref(false)
const createError = ref<string | null>(null)
const draft = ref({ key: '', name: '', description: '' })
const importing = ref(false)

// Filtro "Trilha" das Etapas (D-164, D-165): só as Etapas da última versão publicada da trilha, na ordem dos passos.
// Fica na URL (`?trilha=`) para o link poder ser compartilhado.
const queryPipeline = () => (props.kind === 'stages' && typeof route.query.trilha === 'string' && route.query.trilha) || null
const pipelineKey = ref<string | null>(queryPipeline())
const pipelines = ref<Schemas['PipelineSummary'][]>([])
const selectedPipeline = computed(() => pipelines.value.find((p) => p.key === pipelineKey.value) ?? null)

const headers = [
  { title: t('catalog.key'), key: 'key', sortable: false },
  { title: t('catalog.name'), key: 'name', sortable: false },
  { title: t('catalog.published'), key: 'latestPublishedVersion', sortable: false },
  { title: t('catalog.draft'), key: 'draftVersion', sortable: false },
  ...(props.kind === 'stages' ? [{ title: t('catalog.usedBy'), key: 'usedByPipelines', sortable: false }] : []),
  { title: t('catalog.updated'), key: 'updatedAt', sortable: false },
]

async function load() {
  const query = {
    search: search.value.trim() || undefined,
    status: status.value ?? undefined,
    page: page.value,
    pageSize: pageSize.value,
  }
  const result = await run(() =>
    props.kind === 'stages'
      ? unwrap(api.GET('/stages', { params: { query: { ...query, pipelineKey: pipelineKey.value ?? undefined } } }))
      : unwrap(api.GET('/pipelines', { params: { query } })),
  )
  if (result) {
    items.value = result.items
    total.value = result.total
  }
}

let timer: ReturnType<typeof setTimeout> | null = null
watch(search, () => {
  if (timer) {
    clearTimeout(timer)
  }

  timer = setTimeout(() => {
    page.value = 1
    void load()
  }, 300)
})
watch([status, page, pageSize], () => void load(), { immediate: true })
watch(pipelineKey, (key) => {
  void router.replace({ query: { ...route.query, trilha: key ?? undefined } })
  if (page.value === 1) {
    void load()
  } else {
    page.value = 1
  }
})
watch(
  () => route.query.trilha,
  () => {
    if (queryPipeline() !== pipelineKey.value) {
      pipelineKey.value = queryPipeline()
    }
  },
)

async function loadPipelines() {
  const all: Schemas['PipelineSummary'][] = []
  for (let number = 1; ; number++) {
    const result = await unwrap(api.GET('/pipelines', { params: { query: { page: number, pageSize: 100 } } }))
    all.push(...result.items)
    if (result.items.length === 0 || all.length >= result.total) {
      break
    }
  }

  pipelines.value = all
}

onMounted(() => {
  if (props.kind === 'stages') {
    void run(loadPipelines)
  }
})

const detailRoute = (key: string) => ({ name: props.kind === 'stages' ? 'stage' : 'pipeline', params: { key } })

async function create() {
  createError.value = null
  const body = {
    key: draft.value.key.trim(),
    name: draft.value.name.trim(),
    description: draft.value.description.trim() || null,
  }
  try {
    if (props.kind === 'stages') {
      await unwrap(api.POST('/stages', { body }))
    } else {
      await unwrap(api.POST('/pipelines', { body }))
    }

    creating.value = false
    await router.push({ name: props.kind === 'stages' ? 'stage-editor' : 'pipeline-editor', params: { key: body.key } })
  } catch (error) {
    createError.value = errorText(error)
  }
}

async function importZip(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (!file) {
    return
  }

  importing.value = true
  try {
    const form = new FormData()
    form.append('archive', file, file.name)
    const report = (await (
      await ensureOk(await apiFetch('/import', { method: 'POST', body: form }))
    ).json()) as Schemas['ImportReport']
    notify.success(t('catalog.imported', { count: report.items.length }))
    await load()
  } catch (error) {
    notify.error(errorText(error))
  } finally {
    importing.value = false
    ;(event.target as HTMLInputElement).value = ''
  }
}

function usedBy(item: Summary) {
  return 'usedByPipelines' in item ? t('catalog.usedByCount', { count: item.usedByPipelines }, item.usedByPipelines) : ''
}
</script>

<template>
  <v-container fluid>
    <PageHeader :title="t(`catalog.${kind}.title`)" :subtitle="t(`catalog.${kind}.subtitle`)">
      <template v-if="session.hasRole('Author')">
        <label
          class="v-btn v-btn--density-default v-btn--size-default v-btn--variant-text"
          :class="{ 'v-btn--loading': importing }"
        >
          <v-icon icon="mdi-upload" start aria-hidden="true" />{{ t('catalog.import') }}
          <input type="file" accept=".zip,application/zip" class="d-sr-only" data-testid="import-zip" @change="importZip" />
        </label>
        <v-btn color="primary" prepend-icon="mdi-plus" data-testid="create" @click="creating = true">{{
          t(`catalog.${kind}.create`)
        }}</v-btn>
      </template>
    </PageHeader>
    <v-card>
      <v-card-text class="d-flex flex-wrap ga-3">
        <v-text-field
          v-model="search"
          :label="t('common.search')"
          prepend-inner-icon="mdi-magnify"
          clearable
          hide-details
          style="max-width: 360px"
        />
        <v-select
          v-model="status"
          :items="(['Draft', 'Published', 'Archived'] as const).map((s) => ({ value: s, title: t(`status.version.${s}`) }))"
          :label="t('catalog.status')"
          clearable
          hide-details
          style="max-width: 220px"
        />
        <v-autocomplete
          v-if="kind === 'stages'"
          v-model="pipelineKey"
          :items="pipelines.map((p) => ({ value: p.key, title: p.name }))"
          :label="t('catalog.pipeline')"
          clearable
          hide-details
          style="max-width: 320px"
          data-testid="pipeline-filter"
        />
      </v-card-text>
      <v-alert
        v-if="selectedPipeline && !selectedPipeline.latestPublishedVersion"
        type="info"
        variant="tonal"
        density="compact"
        class="mx-4 mb-2"
        data-testid="pipeline-unpublished"
      >
        {{ t('catalog.pipelineUnpublished') }}
      </v-alert>
      <v-data-table-server
        v-model:page="page"
        v-model:items-per-page="pageSize"
        :headers="headers"
        :items="items"
        :items-length="total"
        :loading="busy"
        :items-per-page-options="[25, 50, 100]"
        data-testid="catalog-table"
        @click:row="(_: unknown, row: { item: Summary }) => router.push(detailRoute(row.item.key))"
      >
        <template #[`item.key`]="{ item }">
          <router-link :to="detailRoute(item.key)" :data-key="item.key">{{ item.key }}</router-link>
        </template>
        <template #[`item.latestPublishedVersion`]="{ item }">
          <v-chip v-if="item.latestPublishedVersion" size="small" color="success" variant="tonal" label
            >v{{ item.latestPublishedVersion }}</v-chip
          >
          <span v-else class="text-medium-emphasis">—</span>
        </template>
        <template #[`item.draftVersion`]="{ item }">
          <v-chip v-if="item.draftVersion" size="small" color="warning" variant="tonal" label>v{{ item.draftVersion }}</v-chip>
        </template>
        <template #[`item.usedByPipelines`]="{ item }">
          <span data-testid="used-by">{{ usedBy(item) }}</span>
        </template>
        <template #[`item.updatedAt`]="{ item }">{{ formatDateTime(item.updatedAt) }}</template>
      </v-data-table-server>
    </v-card>

    <v-dialog v-model="creating" max-width="520" :aria-label="t(`catalog.${kind}.create`)">
      <v-card :title="t(`catalog.${kind}.create`)">
        <v-card-text>
          <v-alert v-if="createError" type="error" variant="tonal" density="compact" class="mb-3">{{ createError }}</v-alert>
          <v-text-field
            v-model="draft.key"
            :label="t('catalog.key')"
            :hint="t('catalog.keyHint')"
            persistent-hint
            data-testid="new-key"
          />
          <v-text-field v-model="draft.name" :label="t('catalog.name')" class="mt-2" data-testid="new-name" />
          <v-textarea v-model="draft.description" :label="t('catalog.description')" rows="2" auto-grow />
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="creating = false">{{ t('common.cancel') }}</v-btn>
          <v-btn
            color="primary"
            :disabled="!draft.key.trim() || !draft.name.trim()"
            data-testid="confirm-create"
            @click="create"
            >{{ t('common.create') }}</v-btn
          >
        </v-card-actions>
      </v-card>
    </v-dialog>
  </v-container>
</template>
