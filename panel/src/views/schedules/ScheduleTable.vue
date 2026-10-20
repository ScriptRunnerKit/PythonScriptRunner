<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, unwrap, type Schemas } from '@/api/client'
import ConfirmDialog from '@/components/common/ConfirmDialog.vue'
import { useApiAction } from '@/composables/useApiAction'
import { useNotify } from '@/stores/notify'
import { useSession } from '@/stores/session'
import { formatDateTime } from '@/utils/format'
import ScheduleForm from './ScheduleForm.vue'

/** Agendamentos (spec 008 §5): próximo e último disparo, último erro destacado; Autor cria, edita, liga/desliga e exclui. */
const props = defineProps<{ pipelineKey?: string }>()
const { t } = useI18n()
const session = useSession()
const notify = useNotify()
const { busy, run } = useApiAction()

const items = ref<Schemas['ScheduleDetail'][]>([])
const page = ref(1)
const total = ref(0)
const pageSize = 25
const editing = ref<Schemas['ScheduleDetail'] | null>(null)
const formOpen = ref(false)
const removing = ref<Schemas['ScheduleDetail'] | null>(null)

async function load() {
  const result = await run(() =>
    unwrap(api.GET('/schedules', { params: { query: { pipelineKey: props.pipelineKey, page: page.value, pageSize } } })),
  )
  if (result) {
    items.value = result.items
    total.value = result.total
  }
}

function open(schedule: Schemas['ScheduleDetail'] | null) {
  editing.value = schedule
  formOpen.value = true
}

function saved() {
  formOpen.value = false
  notify.success(t('schedules.saved'))
  void load()
}

async function toggle(schedule: Schemas['ScheduleDetail'], enabled: boolean) {
  await run(async () => {
    const path = { params: { path: { id: schedule.id } }, headers: { 'If-Match': `"${schedule.rowVersion}"` } }
    await unwrap(enabled ? api.POST('/schedules/{id}/enable', path) : api.POST('/schedules/{id}/disable', path))
  })
  await load()
}

async function remove() {
  const schedule = removing.value
  if (!schedule) {
    return
  }

  await run(() =>
    unwrap(
      api.DELETE('/schedules/{id}', {
        params: { path: { id: schedule.id } },
        headers: { 'If-Match': `"${schedule.rowVersion}"` },
      }),
    ),
  )
  removing.value = null
  await load()
}

onMounted(load)
defineExpose({ create: () => open(null), load })
</script>

<template>
  <div>
    <v-table density="comfortable" data-testid="schedules-table">
      <thead>
        <tr>
          <th>{{ t('schedules.name') }}</th>
          <th v-if="!pipelineKey">{{ t('schedules.pipeline') }}</th>
          <th>{{ t('schedules.cron') }}</th>
          <th>{{ t('schedules.next') }}</th>
          <th>{{ t('schedules.last') }}</th>
          <th>{{ t('schedules.enabled') }}</th>
          <th v-if="session.hasRole('Author')" />
        </tr>
      </thead>
      <tbody>
        <tr v-for="item in items" :key="item.id" :data-schedule="item.data.name">
          <td>
            {{ item.data.name }}
            <div v-if="item.lastError" class="text-caption text-error" data-testid="schedule-error">
              <v-icon icon="mdi-alert-circle" size="14" aria-hidden="true" /> {{ item.lastError }}
            </div>
          </td>
          <td v-if="!pipelineKey">
            <router-link :to="{ name: 'pipeline', params: { key: item.pipelineKey } }">{{ item.pipelineKey }}</router-link>
          </td>
          <td>
            <code>{{ item.data.cron }}</code>
            <div class="text-caption text-medium-emphasis">
              {{ item.data.timeZone }}<template v-if="item.data.pinnedVersion"> · v{{ item.data.pinnedVersion }}</template>
            </div>
          </td>
          <td>{{ formatDateTime(item.nextRunAt) }}</td>
          <td>
            {{ formatDateTime(item.lastFiredAt) }}
            <router-link v-if="item.lastRunId" :to="{ name: 'run', params: { id: item.lastRunId } }" class="text-caption d-block">
              {{ t('schedules.lastRun') }}
            </router-link>
          </td>
          <td>
            <v-switch
              :model-value="item.data.isEnabled"
              :disabled="!session.hasRole('Author') || busy"
              color="primary"
              density="compact"
              hide-details
              inset
              :aria-label="t('schedules.enabled')"
              @update:model-value="(v: boolean | null) => toggle(item, Boolean(v))"
            />
          </td>
          <td v-if="session.hasRole('Author')" class="text-no-wrap">
            <v-btn icon="mdi-pencil" variant="text" size="small" :aria-label="t('common.edit')" @click="open(item)" />
            <v-btn
              icon="mdi-delete-outline"
              variant="text"
              size="small"
              color="error"
              :aria-label="t('common.delete')"
              @click="removing = item"
            />
          </td>
        </tr>
        <tr v-if="!busy && items.length === 0">
          <td colspan="7" class="text-center text-medium-emphasis py-6">{{ t('schedules.empty') }}</td>
        </tr>
      </tbody>
    </v-table>
    <v-pagination
      v-if="total > pageSize"
      v-model="page"
      :length="Math.ceil(total / pageSize)"
      density="comfortable"
      @update:model-value="load"
    />

    <v-dialog v-model="formOpen" max-width="720" scrollable :aria-label="editing ? t('schedules.edit') : t('schedules.create')">
      <ScheduleForm v-if="formOpen" :schedule="editing" :pipeline-key="pipelineKey" @saved="saved" @cancel="formOpen = false" />
    </v-dialog>
    <ConfirmDialog
      :model-value="removing !== null"
      :title="t('schedules.deleteTitle')"
      :text="t('schedules.deleteText', { name: removing?.data.name ?? '' })"
      :confirm="t('common.delete')"
      color="error"
      :busy="busy"
      @update:model-value="(open: boolean) => !open && (removing = null)"
      @confirm="remove"
    />
  </div>
</template>
