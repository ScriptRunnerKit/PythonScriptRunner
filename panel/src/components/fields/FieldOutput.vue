<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { Schemas } from '@/api/client'
import { formatDate, formatDateTime, formatDecimal, formatNumber, formatUtc } from '@/utils/format'
import FileCard from './FileCard.vue'
import JsonTree from './JsonTree.vue'
import { itemField, type FieldDefinition } from './values'

/** Exibição de uma saída conforme o tipo (spec 008, "Widgets por tipo"); arquivos chegam como `{"$artifact": id}`. */
const props = defineProps<{ field?: FieldDefinition; value: unknown; artifacts?: Schemas['ArtifactInfo'][] }>()
const { t } = useI18n()
const copied = ref(false)

const type = computed(() => props.field?.type ?? (typeof props.value === 'object' ? 'json' : 'string'))
const artifact = computed(() => {
  const ref = props.value as { $artifact?: unknown } | null
  const id = ref && typeof ref === 'object' && '$artifact' in ref ? ref.$artifact : null
  const key = typeof id === 'object' && id !== null && 'id' in id ? (id as { id: string }).id : id
  return props.artifacts?.find((a) => a.id === key) ?? null
})
const rows = computed(() => (Array.isArray(props.value) ? (props.value as Record<string, unknown>[]) : []))
const headers = computed(() => {
  const names = props.field?.columns?.map((c) => c.name) ?? [...new Set(rows.value.flatMap((row) => Object.keys(row)))]
  return names.map((name) => ({ title: name, key: name }))
})

async function copy() {
  await navigator.clipboard?.writeText(String(props.value))
  copied.value = true
  setTimeout(() => (copied.value = false), 1500)
}
</script>

<template>
  <div class="field-output">
    <span v-if="value === null || value === undefined" class="text-medium-emphasis">—</span>
    <span v-else-if="type === 'number' || type === 'integer'">{{ formatNumber(value as number) }}</span>
    <span v-else-if="type === 'decimal'">{{ formatDecimal(value as string) }}</span>
    <span v-else-if="type === 'string'" class="d-inline-flex align-center">
      <span class="field-output__text">{{ value }}</span>
      <v-btn
        :icon="copied ? 'mdi-check' : 'mdi-content-copy'"
        variant="text"
        size="x-small"
        :aria-label="t('common.copy')"
        @click="copy"
      />
    </span>
    <v-chip v-else-if="type === 'bool'" size="small" :color="value ? 'success' : 'secondary'" label>
      {{ value ? t('common.yes') : t('common.no') }}
    </v-chip>
    <span v-else-if="type === 'date'">{{ formatDate(value as string) }}</span>
    <v-tooltip v-else-if="type === 'datetime'" :text="formatUtc(value as string)" location="top">
      <template #activator="{ props: tip }">
        <span v-bind="tip">{{ formatDateTime(value as string) }}</span>
      </template>
    </v-tooltip>
    <v-chip v-else-if="type === 'enum'" size="small" label>{{ value }}</v-chip>
    <v-data-table-virtual
      v-else-if="type === 'table'"
      :headers="headers"
      :items="rows"
      density="compact"
      height="260"
      class="border rounded"
    />
    <template v-else-if="type === 'file'">
      <FileCard v-if="artifact" :artifact="artifact" />
      <span v-else class="text-medium-emphasis">{{ t('files.missing') }}</span>
    </template>
    <div v-else-if="type === 'list' && Array.isArray(value) && field">
      <div v-for="(entry, index) in value as unknown[]" :key="index" class="d-flex align-center ga-2">
        <span class="text-medium-emphasis text-caption">{{ index + 1 }}.</span>
        <FieldOutput :field="itemField(field)" :value="entry" :artifacts="artifacts" />
      </div>
    </div>
    <JsonTree v-else :value="value" />
  </div>
</template>

<style scoped>
.field-output__text {
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
