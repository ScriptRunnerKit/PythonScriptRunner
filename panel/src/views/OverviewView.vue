<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, unwrap, type Schemas } from '@/api/client'
import PageHeader from '@/components/common/PageHeader.vue'
import StatusChip from '@/components/common/StatusChip.vue'
import { useApiAction } from '@/composables/useApiAction'
import { throttle, useLive, useNow } from '@/composables/useLive'
import { elapsedMs, formatDateTime, formatDuration, statusColor, statusIcon } from '@/utils/format'

/** Visão geral (QA-008-1, D-115): contadores de 24 h com tendência, em andamento, falhas recentes e próximos agendamentos. */
const { t } = useI18n()
const overview = ref<Schemas['OverviewResponse'] | null>(null)
const { busy, run } = useApiAction()
const now = useNow()

async function load() {
  const data = await run(() => unwrap(api.GET('/overview')))
  if (data) {
    overview.value = data
  }
}

// Cada atualização recarrega os números do banco, no máximo uma vez por segundo (D-150).
const reload = throttle(() => void load(), 1000)
useLive([{ kind: 'all' }], { onRun: reload, poll: reload })
onMounted(load)

const statuses: Schemas['RunStatus'][] = ['Running', 'Queued', 'Pending', 'Succeeded', 'Failed', 'Cancelled']
const counters = computed(() =>
  statuses.map((status) => {
    const current = overview.value?.last24Hours[status] ?? 0
    const previous = overview.value?.previous24Hours[status] ?? 0
    return { status, current, previous, delta: current - previous }
  }),
)

function runTitle(run: Schemas['RunSummary']) {
  return `${run.targetKey}@${run.targetVersion}`
}
</script>

<template>
  <v-container fluid>
    <PageHeader :title="t('overview.title')" :subtitle="t('overview.subtitle')">
      <v-btn variant="text" prepend-icon="mdi-refresh" :loading="busy" @click="load">{{ t('common.refresh') }}</v-btn>
    </PageHeader>

    <v-row density="compact">
      <v-col v-for="counter in counters" :key="counter.status" cols="6" md="4" lg="2">
        <v-card variant="tonal" :color="statusColor(counter.status)" :data-counter="counter.status">
          <v-card-text>
            <div class="d-flex align-center">
              <v-icon :icon="statusIcon(counter.status)" class="mr-2" aria-hidden="true" />
              <span class="text-body-2">{{ t(`status.run.${counter.status}`) }}</span>
            </div>
            <div class="text-h4 mt-2" data-testid="counter-value">{{ counter.current }}</div>
            <div class="text-caption">
              <v-icon
                :icon="counter.delta > 0 ? 'mdi-arrow-up' : counter.delta < 0 ? 'mdi-arrow-down' : 'mdi-minus'"
                size="14"
                aria-hidden="true"
              />
              {{ t('overview.previous', { count: counter.previous }) }}
            </div>
          </v-card-text>
        </v-card>
      </v-col>
    </v-row>

    <v-row class="mt-2">
      <v-col cols="12" lg="6">
        <v-card>
          <v-card-title>{{ t('overview.inProgress') }}</v-card-title>
          <v-list v-if="overview?.inProgress.length" density="compact">
            <v-list-item
              v-for="item in overview.inProgress"
              :key="item.id"
              :to="{ name: 'run', params: { id: item.id } }"
              :title="runTitle(item)"
              :subtitle="`${t(`kind.${item.kind}`)} · ${formatDuration(elapsedMs(item.startedAt ?? item.createdAt, null, now))}`"
            >
              <template #append><StatusChip :status="item.status" /></template>
            </v-list-item>
          </v-list>
          <v-card-text v-else class="text-medium-emphasis">{{ t('overview.noneInProgress') }}</v-card-text>
        </v-card>
      </v-col>
      <v-col cols="12" lg="6">
        <v-card>
          <v-card-title>{{ t('overview.recentFailures') }}</v-card-title>
          <v-list v-if="overview?.recentFailures.length" density="compact">
            <v-list-item
              v-for="item in overview.recentFailures"
              :key="item.id"
              :to="{ name: 'run', params: { id: item.id } }"
              :title="runTitle(item)"
              :subtitle="`${formatDateTime(item.finishedAt ?? item.createdAt)} · ${item.error?.message ?? ''}`"
              prepend-icon="mdi-alert-circle"
              base-color="error"
            />
          </v-list>
          <v-card-text v-else class="text-medium-emphasis">{{ t('overview.noFailures') }}</v-card-text>
        </v-card>
      </v-col>
      <v-col cols="12">
        <v-card>
          <v-card-title>{{ t('overview.upcoming') }}</v-card-title>
          <v-list v-if="overview?.upcomingSchedules.length" density="compact">
            <v-list-item
              v-for="item in overview.upcomingSchedules"
              :key="item.id"
              :to="{ name: 'pipeline', params: { key: item.pipelineKey } }"
              :title="item.name"
              :subtitle="`${item.pipelineKey} · ${formatDateTime(item.nextRunAt)}`"
              prepend-icon="mdi-calendar-clock"
            />
          </v-list>
          <v-card-text v-else class="text-medium-emphasis">{{ t('overview.noSchedules') }}</v-card-text>
        </v-card>
      </v-col>
    </v-row>
  </v-container>
</template>
