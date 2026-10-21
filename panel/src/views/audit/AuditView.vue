<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, unwrap, type Schemas } from '@/api/client'
import PageHeader from '@/components/common/PageHeader.vue'
import JsonTree from '@/components/fields/JsonTree.vue'
import { localToOffset } from '@/components/fields/values'
import { useApiAction } from '@/composables/useApiAction'
import { formatDateTime } from '@/utils/format'

/** Auditoria (Admin; spec 008 §6): lista por cursor, filtros e diff legível (`{campo: [antes, depois]}`). */
const { t } = useI18n()
const { busy, run } = useApiAction()
const filters = reactive({ actor: '', action: '', entityType: '', entityKey: '', from: '', to: '' })
const items = ref<Schemas['AuditEvent'][]>([])
const cursor = ref<string | null>(null)

async function load(reset: boolean) {
  const page = await run(() =>
    unwrap(
      api.GET('/audit', {
        params: {
          query: {
            actor: filters.actor.trim() || undefined,
            action: filters.action.trim() || undefined,
            entityType: filters.entityType.trim() || undefined,
            entityKey: filters.entityKey.trim() || undefined,
            from: filters.from ? (localToOffset(filters.from) ?? undefined) : undefined,
            to: filters.to ? (localToOffset(filters.to) ?? undefined) : undefined,
            cursor: reset ? undefined : (cursor.value ?? undefined),
            limit: 50,
          },
        },
      }),
    ),
  )
  if (page) {
    items.value = reset ? page.items : [...items.value, ...page.items]
    cursor.value = page.nextCursor ?? null
  }
}

/** Mudanças no formato `{campo: [antes, depois]}`; o resto aparece como árvore. */
function diff(details: unknown): { field: string; before: unknown; after: unknown }[] | null {
  if (!details || typeof details !== 'object') {
    return null
  }

  const entries = Object.entries(details as Record<string, unknown>)
  return entries.length > 0 && entries.every(([, v]) => Array.isArray(v) && v.length === 2)
    ? entries.map(([field, v]) => ({ field, before: (v as unknown[])[0], after: (v as unknown[])[1] }))
    : null
}

function show(value: unknown) {
  return value === null || value === undefined ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value)
}

onMounted(() => load(true))
</script>

<template>
  <v-container fluid>
    <PageHeader :title="t('audit.title')" :subtitle="t('audit.subtitle')" />
    <v-card class="mb-4">
      <v-card-text>
        <v-row density="compact">
          <v-col cols="12" md="2"
            ><v-text-field v-model="filters.actor" :label="t('audit.actor')" hide-details clearable
          /></v-col>
          <v-col cols="12" md="2"
            ><v-text-field v-model="filters.action" :label="t('audit.action')" hide-details clearable
          /></v-col>
          <v-col cols="12" md="2"
            ><v-text-field v-model="filters.entityType" :label="t('audit.entityType')" hide-details clearable
          /></v-col>
          <v-col cols="12" md="2"
            ><v-text-field v-model="filters.entityKey" :label="t('audit.entityKey')" hide-details clearable
          /></v-col>
          <v-col cols="12" md="2"
            ><v-text-field v-model="filters.from" type="datetime-local" :label="t('runs.filters.from')" hide-details
          /></v-col>
          <v-col cols="12" md="2"
            ><v-text-field v-model="filters.to" type="datetime-local" :label="t('runs.filters.to')" hide-details
          /></v-col>
        </v-row>
        <div class="d-flex mt-3">
          <span class="text-caption text-medium-emphasis mr-auto">{{ t('runs.filters.window') }}</span>
          <v-btn color="primary" prepend-icon="mdi-filter" :loading="busy" @click="load(true)">{{ t('common.filter') }}</v-btn>
        </div>
      </v-card-text>
    </v-card>
    <v-card>
      <v-table density="comfortable" data-testid="audit-table">
        <thead>
          <tr>
            <th>{{ t('audit.when') }}</th>
            <th>{{ t('audit.actor') }}</th>
            <th>{{ t('audit.action') }}</th>
            <th>{{ t('audit.entity') }}</th>
            <th>{{ t('audit.details') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.id" :data-action="item.action">
            <td class="text-no-wrap">{{ formatDateTime(item.occurredAt) }}</td>
            <td>{{ item.actor }}</td>
            <td>{{ t(`auditActions.${item.action}`, item.action) }}</td>
            <td>
              {{ item.entityType }}<span v-if="item.entityKey" class="text-medium-emphasis"> · {{ item.entityKey }}</span>
            </td>
            <td>
              <template v-if="diff(item.details)">
                <div v-for="change in diff(item.details)" :key="change.field" class="text-caption">
                  <strong>{{ change.field }}</strong
                  >: {{ show(change.before) }} → {{ show(change.after) }}
                </div>
              </template>
              <JsonTree v-else-if="item.details" :value="item.details" />
            </td>
          </tr>
          <tr v-if="!busy && items.length === 0">
            <td colspan="5" class="text-center text-medium-emphasis py-6">{{ t('audit.empty') }}</td>
          </tr>
        </tbody>
      </v-table>
      <div v-if="cursor" class="text-center pa-2">
        <v-btn variant="text" :loading="busy" @click="load(false)">{{ t('common.loadMore') }}</v-btn>
      </div>
    </v-card>
  </v-container>
</template>
