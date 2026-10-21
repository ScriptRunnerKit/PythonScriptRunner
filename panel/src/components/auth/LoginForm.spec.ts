import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import LoginForm from './LoginForm.vue'

describe('Formulário de login fixo', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('Spec008_AC17 na página o usuário já recebe o foco ao montar (quem digita logo não perde a senha)', () => {
    const wrapper = mount(LoginForm, { attachTo: document.body })

    expect(document.activeElement).toBe(wrapper.find('input[name=name]').element)
    wrapper.unmount()
  })

  it('Spec008_AC19 com o usuário travado (sessão expirada) o campo de usuário não toma o foco', () => {
    const wrapper = mount(LoginForm, { attachTo: document.body, props: { lockedName: 'ana' } })

    expect(document.activeElement).not.toBe(wrapper.find('input[name=name]').element)
    expect(wrapper.find('input[name=name]').attributes('readonly')).toBeDefined()
    wrapper.unmount()
  })
})
