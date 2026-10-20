<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import FieldInput from './FieldInput.vue'
import { fromWire, toWire, type ColumnKind, type FieldDefinition, type FieldKind, type UiValue } from './values'

/**
 * Construtor de campos (spec 008): nome, tipo (12 tipos), obrigatório, default (widget do tipo), descrição,
 * `enumValues`, `itemType`, `contentTypes` e `columns`.
 */
const props = withDefaults(defineProps<{ kind: 'inputs' | 'outputs'; readonly?: boolean }>(), { readonly: false })
const fields = defineModel<FieldDefinition[]>({ required: true })
const { t } = useI18n()

const types: FieldKind[] = [
  'number',
  'integer',
  'decimal',
  'string',
  'bool',
  'date',
  'datetime',
  'enum',
  'json',
  'table',
  'file',
  'list',
]
const itemTypes = types.filter((x): x is Exclude<FieldKind, 'list'> => x !== 'list')
const columnTypes: ColumnKind[] = ['number', 'integer', 'decimal', 'string', 'bool', 'date', 'datetime']
const namePattern = /^[a-z_][a-z0-9_]{0,63}$/

const duplicates = computed(() => {
  const seen = new Set<string>()
  const repeated = new Set<string>()
  fields.value.forEach((f) => (seen.has(f.name) ? repeated.add(f.name) : seen.add(f.name)))
  return repeated
})

function nameError(field: FieldDefinition): string[] {
  if (!namePattern.test(field.name)) {
    return [t('fieldBuilder.nameInvalid')]
  }

  return duplicates.value.has(field.name) ? [t('fieldBuilder.nameDuplicated')] : []
}

function update(index: number, changes: Partial<FieldDefinition>) {
  const next = [...fields.value]
  const field = { ...next[index]!, ...changes }
  if (field.type !== 'list') {
    delete field.itemType
  }

  if (field.type !== 'enum' && field.itemType !== 'enum') {
    delete field.enumValues
  }

  if (field.type !== 'table') {
    delete field.columns
  }

  if (field.type !== 'file' && field.itemType !== 'file') {
    delete field.contentTypes
  }

  if (!allowsDefault(field)) {
    delete field.default
  }

  next[index] = field
  fields.value = next
}

function allowsDefault(field: FieldDefinition) {
  return props.kind === 'inputs' && field.type !== 'file' && field.type !== 'table' && field.itemType !== 'file'
}

function add() {
  let n = fields.value.length + 1
  while (fields.value.some((f) => f.name === `campo_${n}`)) {
    n++
  }

  fields.value = [...fields.value, { name: `campo_${n}`, type: 'string', required: true }]
}

function remove(index: number) {
  fields.value = fields.value.filter((_, i) => i !== index)
}

function move(index: number, delta: number) {
  const next = [...fields.value]
  const target = index + delta
  if (target < 0 || target >= next.length) {
    return
  }

  ;[next[index], next[target]] = [next[target]!, next[index]!]
  fields.value = next
}

function defaultValue(field: FieldDefinition): UiValue {
  return fromWire({ ...field, required: false }, field.default)
}

function setDefault(index: number, field: FieldDefinition, value: UiValue) {
  const wire = toWire({ ...field, required: false, default: undefined }, value)
  update(index, { default: wire === undefined ? undefined : wire })
}

function setColumn(index: number, field: FieldDefinition, column: number, changes: { name?: string; type?: ColumnKind }) {
  const columns = [...(field.columns ?? [])]
  columns[column] = { ...columns[column]!, ...changes }
  update(index, { columns })
}
</script>

<template>
  <div data-testid="field-builder" :data-kind="kind">
    <p v-if="fields.length === 0" class="text-medium-emphasis mb-2">{{ t(`fieldBuilder.empty.${kind}`) }}</p>
    <v-expansion-panels variant="accordion" multiple>
      <v-expansion-panel v-for="(field, index) in fields" :key="index" :data-field="field.name">
        <v-expansion-panel-title>
          <div class="d-flex align-center ga-2">
            <strong>{{ field.name }}</strong>
            <v-chip size="x-small" label>{{ field.type === 'list' ? `list<${field.itemType ?? '?'}>` : field.type }}</v-chip>
            <v-chip v-if="field.required === false" size="x-small" variant="outlined" label>{{
              t('fieldBuilder.optional')
            }}</v-chip>
            <v-icon v-if="nameError(field).length" icon="mdi-alert-circle" color="error" size="small" aria-hidden="true" />
          </div>
        </v-expansion-panel-title>
        <v-expansion-panel-text>
          <v-row dense>
            <v-col cols="12" md="5">
              <v-text-field
                :model-value="field.name"
                :label="t('fieldBuilder.name')"
                :error-messages="nameError(field)"
                :readonly="readonly"
                @update:model-value="(v: string) => update(index, { name: v })"
              />
            </v-col>
            <v-col cols="12" md="4">
              <v-select
                :model-value="field.type"
                :items="types"
                :label="t('fieldBuilder.type')"
                :readonly="readonly"
                @update:model-value="
                  (v: FieldKind) =>
                    update(index, {
                      type: v,
                      itemType: v === 'list' ? (field.itemType ?? 'string') : undefined,
                    })
                "
              />
            </v-col>
            <v-col cols="12" md="3">
              <v-switch
                :model-value="field.required !== false"
                :label="t('fieldBuilder.required')"
                :readonly="readonly"
                color="primary"
                inset
                @update:model-value="(v: boolean | null) => update(index, { required: Boolean(v) })"
              />
            </v-col>
            <v-col v-if="field.type === 'list'" cols="12" md="4">
              <v-select
                :model-value="field.itemType ?? 'string'"
                :items="itemTypes"
                :label="t('fieldBuilder.itemType')"
                :readonly="readonly"
                @update:model-value="(v: Exclude<FieldKind, 'list'>) => update(index, { itemType: v })"
              />
            </v-col>
            <v-col v-if="field.type === 'enum' || field.itemType === 'enum'" cols="12">
              <v-combobox
                :model-value="field.enumValues ?? []"
                :label="t('fieldBuilder.enumValues')"
                :readonly="readonly"
                multiple
                chips
                closable-chips
                @update:model-value="(v: string[]) => update(index, { enumValues: v })"
              />
            </v-col>
            <v-col v-if="field.type === 'file' || field.itemType === 'file'" cols="12">
              <v-combobox
                :model-value="field.contentTypes ?? []"
                :label="t('fieldBuilder.contentTypes')"
                :hint="t('fieldBuilder.contentTypesHint')"
                :readonly="readonly"
                persistent-hint
                multiple
                chips
                closable-chips
                @update:model-value="(v: string[]) => update(index, { contentTypes: v.length ? v : undefined })"
              />
            </v-col>
            <v-col v-if="field.type === 'table'" cols="12">
              <div class="text-body-2 mb-2">{{ t('fieldBuilder.columns') }}</div>
              <div v-for="(column, c) in field.columns ?? []" :key="c" class="d-flex ga-2 align-start">
                <v-text-field
                  :model-value="column.name"
                  :label="t('fieldBuilder.columnName')"
                  :readonly="readonly"
                  @update:model-value="(v: string) => setColumn(index, field, c, { name: v })"
                />
                <v-select
                  :model-value="column.type"
                  :items="columnTypes"
                  :label="t('fieldBuilder.type')"
                  :readonly="readonly"
                  @update:model-value="(v: ColumnKind) => setColumn(index, field, c, { type: v })"
                />
                <v-btn
                  v-if="!readonly"
                  icon="mdi-close"
                  variant="text"
                  class="mt-2"
                  :aria-label="t('fieldBuilder.removeColumn')"
                  @click="update(index, { columns: (field.columns ?? []).filter((_, i) => i !== c) })"
                />
              </div>
              <v-btn
                v-if="!readonly"
                size="small"
                variant="text"
                prepend-icon="mdi-plus"
                @click="update(index, { columns: [...(field.columns ?? []), { name: 'coluna', type: 'string' }] })"
              >
                {{ t('fieldBuilder.addColumn') }}
              </v-btn>
            </v-col>
            <v-col v-if="allowsDefault(field)" cols="12">
              <FieldInput
                :field="{
                  ...field,
                  name: t('fieldBuilder.default'),
                  required: false,
                  description: t('fieldBuilder.defaultHint'),
                }"
                :model-value="defaultValue(field)"
                :disabled="readonly"
                @update:model-value="(v: UiValue) => setDefault(index, field, v)"
              />
            </v-col>
            <v-col cols="12">
              <v-textarea
                :model-value="field.description ?? ''"
                :label="t('fieldBuilder.description')"
                :readonly="readonly"
                rows="1"
                auto-grow
                counter="500"
                @update:model-value="(v: string) => update(index, { description: v || undefined })"
              />
            </v-col>
          </v-row>
          <div v-if="!readonly" class="d-flex ga-1">
            <v-btn
              size="small"
              variant="text"
              icon="mdi-arrow-up"
              :aria-label="t('fieldBuilder.moveUp')"
              @click="move(index, -1)"
            />
            <v-btn
              size="small"
              variant="text"
              icon="mdi-arrow-down"
              :aria-label="t('fieldBuilder.moveDown')"
              @click="move(index, 1)"
            />
            <v-spacer />
            <v-btn size="small" variant="text" color="error" prepend-icon="mdi-delete-outline" @click="remove(index)">
              {{ t('fieldBuilder.remove') }}
            </v-btn>
          </div>
        </v-expansion-panel-text>
      </v-expansion-panel>
    </v-expansion-panels>
    <v-btn v-if="!readonly" class="mt-3" variant="tonal" prepend-icon="mdi-plus" data-testid="add-field" @click="add">
      {{ t(`fieldBuilder.add.${kind}`) }}
    </v-btn>
  </div>
</template>
