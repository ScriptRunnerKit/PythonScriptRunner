<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { api, unwrap, type Schemas } from '@/api/client'
import PageHeader from '@/components/common/PageHeader.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import { localToOffset } from '@/components/fields/values'
import { useApiAction } from '@/composables/useApiAction'
import { throttle, useLive, useNow } from '@/composables/useLive'
import { elapsedMs, formatDateTime, formatDuration } from '@/utils/format'

type RunSummary = Schemas['RunSummary']

/** Execuções (spec 008): filtros, *scroll* infinito por cursor e atualização ao vivo. */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const now = useNow()
const { busy, run } = useApiAction()

const kinds: Schemas['RunKind'][] = ['Pipeline', 'PipelineTest', 'Stage', 'StageTest']
const statuses: Schemas['RunStatus'][] = ['Pending', 'Queued', 'Running', 'Succeeded', 'Failed', 'Cancelling', 'Cancelled']
const triggers: Schemas['RunTrigger'][] = ['Library', 'Dashboard', 'Schedule', 'QueueCommand']

const filters = reactive({
  pipelineKey: (route.query.pipelineKey as string) ?? '',
  stageKey: (route.query.stageKey as string) ?? '',
  kind: (route.query.kind as Schemas['RunKind'] | undefined) ?? null,
  status: (route.query.status as Schemas['RunStatus'] | undefined) ?? null,
  trigger: (route.query.trigger as Schemas['RunTrigger'] | undefined) ?? null,
  from: (route.query.from as string) ?? '',
  to: (route.query.to as string) ?? '',
})
const items = ref<RunSummary[]>([])
const cursor = ref<string | null>(null)
const done = ref(false)

const query = computed(() => ({
  pipelineKey: filters.pipelineKey.trim() || undefined,
  stageKey: filters.stageKey.trim() || undefined,
  kind: filters.kind ?? undefined,
  status: filters.status ?? undefined,
  trigger: filters.trigger ?? undefined,
  from: filters.from ? (localToOffset(filters.from) ?? undefined) : undefined,
  to: filters.to ? (localToOffset(filters.to) ?? undefined) : undefined,
  limit: 50,
}))

async function load(reset: boolean) {
  if (!reset && done.value) {
    return
  }

  const page = await run(() =>
    unwrap(api.GET('/runs', { params: { query: { ...query.value, cursor: reset ? undefined : (cursor.value ?? undefined) } } })),
  )
  if (!page) {
    return
  }

  items.value = reset ? page.items : [...items.value, ...page.items]
  cursor.value = page.nextCursor ?? null
  done.value = page.nextCursor === null || page.nextCursor === undefined
}

function apply() {
  void router.replace({ query: Object.fromEntries(Object.entries(filters).filter(([, v]) => v)) })
  void load(true)
}

function matches(item: RunSummary): boolean {
  const q = query.value
  return (
    (!q.pipelineKey || (item.kind.startsWith('Pipeline') && item.targetKey === q.pipelineKey)) &&
    (!q.stageKey || (item.kind.startsWith('Stage') && item.targetKey === q.stageKey)) &&
    (!q.kind || item.kind === q.kind) &&
    (!q.status || item.status === q.status) &&
    (!q.trigger || item.trigger === q.trigger) &&
    !q.to
  )
}

const refresh = throttle(() => void load(true), 1000)
useLive([{ kind: 'all' }], {
  onRun: (update) => {
    const index = items.value.findIndex((x) => x.id === update.run.id)
    if (index >= 0) {
      items.value.splice(index, 1, matches(update.run) || !filters.status ? update.run : items.value[index]!)
    } else if (matches(update.run)) {
      items.value.unshift(update.run)
    }
  },
  poll: refresh,
})

onMounted(() => load(true))

function onScroll({ done: finish }: { done: (status: 'ok' | 'empty' | 'error') => void }) {
  void load(false).then(() => finish(done.value ? 'empty' : 'ok'))
}
</script>

<template>
  <v-container fluid>
    <PageHeader :title="t('runs.title')" :subtitle="t('runs.subtitle')" />
    <v-card class="mb-4">
      <v-card-text>
        <v-row dense>
          <v-col cols="12" md="3"
            ><v-text-field v-model="filters.pipelineKey" :label="t('runs.filters.pipeline')" clearable hide-details
          /></v-col>
          <v-col cols="12" md="3"
            ><v-text-field v-model="filters.stageKey" :label="t('runs.filters.stage')" clearable hide-details
          /></v-col>
          <v-col cols="12" md="2">
            <v-select
              v-model="filters.kind"
              :items="kinds.map((k) => ({ value: k, title: t(`kind.${k}`) }))"
              :label="t('runs.filters.kind')"
              clearable
              hide-details
            />
          </v-col>
          <v-col cols="12" md="2">
            <v-select
              v-model="filters.status"
              :items="statuses.map((s) => ({ value: s, title: t(`status.run.${s}`) }))"
              :label="t('runs.filters.status')"
              clearable
              hide-details
            />
          </v-col>
          <v-col cols="12" md="2">
            <v-select
              v-model="filters.trigger"
              :items="triggers.map((x) => ({ value: x, title: t(`trigger.${x}`) }))"
              :label="t('runs.filters.trigger')"
              clearable
              hide-details
            />
          </v-col>
          <v-col cols="12" md="3"
            ><v-text-field v-model="filters.from" type="datetime-local" :label="t('runs.filters.from')" hide-details
          /></v-col>
          <v-col cols="12" md="3"
            ><v-text-field v-model="filters.to" type="datetime-local" :label="t('runs.filters.to')" hide-details
          /></v-col>
          <v-col cols="12" md="6" class="d-flex align-center">
            <span class="text-caption text-medium-emphasis mr-auto">{{ t('runs.filters.window') }}</span>
            <v-btn color="primary" prepend-icon="mdi-filter" :loading="busy" @click="apply">{{ t('common.filter') }}</v-btn>
          </v-col>
        </v-row>
      </v-card-text>
    </v-card>

    <v-card>
      <v-table density="comfortable" data-testid="runs-table">
        <thead>
          <tr>
            <th>{{ t('runs.columns.status') }}</th>
            <th>{{ t('runs.columns.target') }}</th>
            <th>{{ t('runs.columns.kind') }}</th>
            <th>{{ t('runs.columns.trigger') }}</th>
            <th>{{ t('runs.columns.created') }}</th>
            <th>{{ t('runs.columns.duration') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="item in items"
            :key="item.id"
            class="cursor-pointer"
            :data-run="item.id"
            @click="router.push({ name: 'run', params: { id: item.id } })"
          >
            <td><StatusChip :status="item.status" /></td>
            <td>
              <router-link :to="{ name: 'run', params: { id: item.id } }" @click.stop
                >{{ item.targetKey }}@{{ item.targetVersion }}</router-link
              >
              <div v-if="item.error" class="text-caption text-error text-truncate" style="max-width: 360px">
                {{ item.error.message }}
              </div>
            </td>
            <td>{{ t(`kind.${item.kind}`) }}</td>
            <td>
              {{ t(`trigger.${item.trigger}`) }}
              <div class="text-caption text-medium-emphasis">{{ item.triggeredBy }}</div>
            </td>
            <td>{{ formatDateTime(item.createdAt) }}</td>
            <td>{{ formatDuration(elapsedMs(item.startedAt, item.finishedAt, now)) }}</td>
          </tr>
          <tr v-if="!busy && items.length === 0">
            <td colspan="6" class="text-center text-medium-emphasis py-6">{{ t('runs.empty') }}</td>
          </tr>
        </tbody>
      </v-table>
      <v-infinite-scroll v-if="items.length > 0 && !done" :items="items" @load="onScroll">
        <template #empty />
      </v-infinite-scroll>
    </v-card>
  </v-container>
</template>
