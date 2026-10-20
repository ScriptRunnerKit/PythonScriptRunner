import { defineStore } from 'pinia'
import { ref } from 'vue'

export type ThemeChoice = 'system' | 'light' | 'dark'

const storageKey = 'psr.theme'

function read(): ThemeChoice {
  try {
    const value = window.localStorage.getItem(storageKey)
    return value === 'light' || value === 'dark' ? value : 'system'
  } catch {
    return 'system'
  }
}

/** Tema claro/escuro seguindo o sistema, com alternância manual persistida (spec 008, AC-12). */
export const useThemeChoice = defineStore('theme', () => {
  const choice = ref<ThemeChoice>(read())
  const systemDark = ref(window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false)
  window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener?.('change', (event) => (systemDark.value = event.matches))

  function resolved(): 'light' | 'dark' {
    return choice.value === 'system' ? (systemDark.value ? 'dark' : 'light') : choice.value
  }

  function set(value: ThemeChoice) {
    choice.value = value
    try {
      if (value === 'system') {
        window.localStorage.removeItem(storageKey)
      } else {
        window.localStorage.setItem(storageKey, value)
      }
    } catch {
      // Sem armazenamento: vale só nesta aba.
    }
  }

  return { choice, systemDark, resolved, set }
})
