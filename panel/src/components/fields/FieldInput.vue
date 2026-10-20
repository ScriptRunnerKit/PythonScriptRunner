<script setup lang="ts">
import { computed, defineAsyncComponent } from 'vue'
import { useI18n } from 'vue-i18n'
import { emptyValue, itemField, parseCsv, type FieldDefinition, type UiValue } from './values'

/** Widget de entrada de um campo conforme o tipo (spec 008, "Widgets por tipo"). */
const props = defineProps<{ field: FieldDefinition; error?: string | null; disabled?: boolean; hideLabel?: boolean }>()
const model = defineModel<UiValue>({ required: true })
const { t } = useI18n()

const CodeEditor = defineAsyncComponent(() => import('@/components/editor/CodeEditor.vue'))

const label = computed(() => (props.hideLabel ? undefined : `${props.field.name}${props.field.required === false ? '' : ' *'}`))
const hint = computed(() => props.field.description ?? undefined)
const errors = computed(() => (props.error ? [props.error] : []))
const text = computed<string>({
  get: () => (typeof model.value === 'string' ? model.value : ''),
  set: (value) => (model.value = value),
})
const accept = computed(() => props.field.contentTypes?.join(',') ?? undefined)
const offset = computed(() => {
  const minutes = -new Date().getTimezoneOffset()
  const sign = minutes >= 0 ? '+' : '-'
  return `UTC${sign}${String(Math.floor(Math.abs(minutes) / 60)).padStart(2, '0')}:${String(Math.abs(minutes) % 60).padStart(2, '0')}`
})
const item = computed(() => itemField(props.field))
const items = computed<UiValue[]>(() => (Array.isArray(model.value) ? (model.value as UiValue[]) : []))
const file = computed(() => (model.value instanceof File ? model.value : null))
const files = computed(() => items.value.filter((x): x is File => x instanceof File))

function setFile(value: File | File[] | null | undefined) {
  model.value = Array.isArray(value) ? (value[0] ?? null) : (value ?? null)
}

function setFiles(value: File | File[] | null | undefined) {
  model.value = Array.isArray(value) ? value : value ? [value] : []
}

function setItem(index: number, value: UiValue) {
  const next = [...items.value]
  next[index] = value
  model.value = next
}

function addItem() {
  model.value = [...items.value, emptyValue(item.value)]
}

function removeItem(index: number) {
  model.value = items.value.filter((_, i) => i !== index)
}

async function importCsv(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (file) {
    model.value = JSON.stringify(parseCsv(await file.text(), props.field.columns), null, 2)
  }
}
</script>

<template>
  <div class="field-input" :data-field="field.name">
    <v-text-field
      v-if="field.type === 'number' || field.type === 'integer' || field.type === 'decimal'"
      v-model="text"
      :label="label"
      :hint="hint"
      :error-messages="errors"
      :disabled="disabled"
      :inputmode="field.type === 'integer' ? 'numeric' : 'decimal'"
      persistent-hint
    />
    <v-textarea
      v-else-if="field.type === 'string'"
      v-model="text"
      :label="label"
      :hint="hint"
      :error-messages="errors"
      :disabled="disabled"
      rows="1"
      auto-grow
      persistent-hint
    />
    <v-switch
      v-else-if="field.type === 'bool'"
      v-model="model"
      :label="label"
      :hint="hint"
      :error-messages="errors"
      :disabled="disabled"
      color="primary"
      inset
      persistent-hint
    />
    <v-text-field
      v-else-if="field.type === 'date'"
      v-model="text"
      type="date"
      :label="label"
      :hint="hint"
      :error-messages="errors"
      :disabled="disabled"
      persistent-hint
    />
    <v-text-field
      v-else-if="field.type === 'datetime'"
      v-model="text"
      type="datetime-local"
      step="1"
      :label="label"
      :hint="hint ?? t('fields.datetimeHint', { offset })"
      :error-messages="errors"
      :disabled="disabled"
      persistent-hint
    />
    <v-select
      v-else-if="field.type === 'enum'"
      v-model="text"
      :items="field.enumValues ?? []"
      :label="label"
      :hint="hint"
      :error-messages="errors"
      :disabled="disabled"
      :clearable="field.required === false"
      persistent-hint
    />
    <div v-else-if="field.type === 'json' || field.type === 'table'">
      <div class="d-flex align-center mb-1">
        <span class="text-body-2">{{ label }}</span>
        <v-spacer />
        <label
          v-if="field.type === 'table' && !disabled"
          class="v-btn v-btn--density-compact v-btn--size-small v-btn--variant-text"
        >
          <v-icon icon="mdi-file-delimited-outline" start aria-hidden="true" />{{ t('fields.importCsv') }}
          <input type="file" accept=".csv,text/csv" class="d-sr-only" @change="importCsv" />
        </label>
      </div>
      <CodeEditor v-model="text" language="json" height="160px" :readonly="disabled" :label="field.name" />
      <div v-if="errors.length" class="text-error text-caption mt-1">{{ errors[0] }}</div>
      <div v-else-if="hint" class="text-medium-emphasis text-caption mt-1">{{ hint }}</div>
    </div>
    <v-file-input
      v-else-if="field.type === 'file'"
      :model-value="file"
      :label="label"
      :hint="hint"
      :accept="accept"
      :error-messages="errors"
      :disabled="disabled"
      show-size
      persistent-hint
      prepend-icon=""
      prepend-inner-icon="mdi-paperclip"
      @update:model-value="setFile"
    />
    <v-file-input
      v-else-if="field.type === 'list' && field.itemType === 'file'"
      :model-value="files"
      :label="label"
      :hint="hint"
      :accept="accept"
      :error-messages="errors"
      :disabled="disabled"
      multiple
      chips
      show-size
      persistent-hint
      prepend-icon=""
      prepend-inner-icon="mdi-paperclip"
      @update:model-value="setFiles"
    />
    <fieldset v-else-if="field.type === 'list'" class="field-input__list">
      <legend class="text-body-2 px-1">{{ label }}</legend>
      <div v-for="(value, index) in items" :key="index" class="d-flex align-start ga-2">
        <FieldInput
          :model-value="value"
          :field="{ ...item, name: `${field.name}[${index}]` }"
          :disabled="disabled"
          class="flex-grow-1"
          @update:model-value="(v: UiValue) => setItem(index, v)"
        />
        <v-btn
          icon="mdi-close"
          variant="text"
          size="small"
          class="mt-2"
          :disabled="disabled"
          :aria-label="t('fields.removeItem', { index: index + 1 })"
          @click="removeItem(index)"
        />
      </div>
      <v-btn variant="text" size="small" prepend-icon="mdi-plus" :disabled="disabled" @click="addItem">{{
        t('fields.addItem')
      }}</v-btn>
      <div v-if="errors.length" class="text-error text-caption">{{ errors[0] }}</div>
      <div v-else-if="hint" class="text-medium-emphasis text-caption">{{ hint }}</div>
    </fieldset>
  </div>
</template>

<style scoped>
.field-input__list {
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 4px;
  padding: 8px 12px;
  margin-bottom: 16px;
}
</style>
