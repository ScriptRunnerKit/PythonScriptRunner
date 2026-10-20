import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import FieldForm from './FieldForm.vue'
import type { FieldDefinition, UiValue } from './values'

/** Spec 008, AC-14: o formulário gerado mostra um widget por tipo e valida antes de enviar. */
const fields: FieldDefinition[] = [
  { name: 'numero', type: 'number' },
  { name: 'inteiro', type: 'integer' },
  { name: 'valor', type: 'decimal' },
  { name: 'texto', type: 'string' },
  { name: 'ativo', type: 'bool' },
  { name: 'dia', type: 'date' },
  { name: 'quando', type: 'datetime' },
  { name: 'cor', type: 'enum', enumValues: ['azul', 'verde'] },
  { name: 'dados', type: 'json' },
  { name: 'linhas', type: 'table' },
  { name: 'arquivo', type: 'file' },
  { name: 'termos', type: 'list', itemType: 'number' },
]

describe('Spec008_AC14 formulário gerado', () => {
  it('mostra um widget por tipo e aponta os obrigatórios vazios', async () => {
    let values: Record<string, UiValue> = {}
    const wrapper = mount(FieldForm, {
      props: { fields, modelValue: values, 'onUpdate:modelValue': (v: Record<string, UiValue>) => (values = v) },
    })
    await nextTick()

    for (const field of fields) {
      expect(wrapper.find(`[data-field="${field.name}"]`).exists(), field.name).toBe(true)
    }

    expect(wrapper.find('[data-field="dia"] input[type="date"]').exists()).toBe(true)
    expect(wrapper.find('[data-field="quando"] input[type="datetime-local"]').exists()).toBe(true)
    expect(wrapper.find('[data-field="arquivo"] input[type="file"]').exists()).toBe(true)
    expect((wrapper.vm as unknown as { check: () => boolean }).check()).toBe(false)
    await nextTick()
    expect(wrapper.text()).toContain('Obrigatório.')
    expect(Object.keys(values)).toEqual(fields.map((f) => f.name))
  })
})
