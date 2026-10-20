<script setup lang="ts">
import { reactive, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import FieldInput from './FieldInput.vue'
import { emptyValue, validate, type FieldDefinition, type UiValue } from './values'

/**
 * Formulário gerado a partir dos campos declarados (spec 008): testar Stage, disparar trilha e entradas fixas de
 * agendamento. `check()` valida tudo e mostra os erros.
 */
const props = withDefaults(
  defineProps<{ fields: FieldDefinition[]; maxFileBytes?: number; disabled?: boolean; excludeFiles?: boolean }>(),
  {
    maxFileBytes: 50 * 1024 * 1024,
    disabled: false,
    excludeFiles: false,
  },
)
const values = defineModel<Record<string, UiValue>>({ required: true })
const { t } = useI18n()
const errors = reactive<Record<string, string | null>>({})

watch(
  () => props.fields,
  (fields) => {
    const next = { ...values.value }
    for (const field of fields) {
      if (!(field.name in next)) {
        next[field.name] = emptyValue(field)
      }
    }

    values.value = next
  },
  { immediate: true },
)

function message(error: ReturnType<typeof validate>): string | null {
  if (!error) {
    return null
  }

  const params = { ...error.params }
  if (typeof params.reason === 'string') {
    params.reason = t(params.reason, params)
  }

  return t(error.key, params)
}

function check(): boolean {
  let ok = true
  for (const field of props.fields) {
    const error = message(validate(field, values.value[field.name] ?? null, props.maxFileBytes))
    errors[field.name] = error
    ok &&= error === null
  }

  return ok
}

function update(name: string, value: UiValue) {
  values.value = { ...values.value, [name]: value }
  if (errors[name]) {
    errors[name] = null
  }
}

defineExpose({ check })
</script>

<template>
  <div>
    <p v-if="fields.length === 0" class="text-medium-emphasis">{{ t('fields.noInputs') }}</p>
    <FieldInput
      v-for="field in fields"
      :key="field.name"
      :field="field"
      :model-value="values[field.name] ?? null"
      :error="errors[field.name]"
      :disabled="disabled || (excludeFiles && (field.type === 'file' || field.itemType === 'file'))"
      @update:model-value="(v: UiValue) => update(field.name, v)"
    />
  </div>
</template>
