import { defineStore } from 'pinia'
import { ref } from 'vue'

export type NoticeKind = 'success' | 'info' | 'warning' | 'error'

export interface Notice {
  id: number
  kind: NoticeKind
  text: string
}

/** Avisos curtos (snackbar) da aplicação. */
export const useNotify = defineStore('notify', () => {
  const current = ref<Notice | null>(null)
  const queue: Notice[] = []
  let sequence = 0

  function push(kind: NoticeKind, text: string) {
    const notice = { id: ++sequence, kind, text }
    if (current.value === null) {
      current.value = notice
    } else {
      queue.push(notice)
    }
  }

  function next() {
    current.value = queue.shift() ?? null
  }

  return {
    current,
    next,
    success: (text: string) => push('success', text),
    info: (text: string) => push('info', text),
    warning: (text: string) => push('warning', text),
    error: (text: string) => push('error', text),
  }
})
