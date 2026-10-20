import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue'
import { useRealtime, type LiveHandlers, type Subscription } from '@/stores/realtime'

/** Acompanha grupos do hub enquanto o componente está montado (e o *polling* de 5 s sem conexão). */
export function useLive(subscriptions: Subscription[] | (() => Subscription[]), handlers: LiveHandlers) {
  const realtime = useRealtime()
  let stop: (() => void) | null = null

  function start() {
    stop?.()
    stop = realtime.watch(typeof subscriptions === 'function' ? subscriptions() : subscriptions, handlers)
  }

  onMounted(start)
  onBeforeUnmount(() => stop?.())
  return { restart: start }
}

/** Relógio local para durações ao vivo (sem requisições; spec 008). */
export function useNow(intervalMs = 1000): Ref<number> {
  const now = ref(Date.now())
  let timer: ReturnType<typeof setInterval> | null = null
  onMounted(() => (timer = setInterval(() => (now.value = Date.now()), intervalMs)))
  onBeforeUnmount(() => timer && clearInterval(timer))
  return now
}

/** Executa no máximo uma vez por janela, sem perder a última chamada (recarregar /overview; D-150). */
export function throttle(action: () => void, windowMs: number): () => void {
  let last = 0
  let pending: ReturnType<typeof setTimeout> | null = null
  return () => {
    const wait = last + windowMs - Date.now()
    if (wait <= 0) {
      last = Date.now()
      action()
    } else if (pending === null) {
      pending = setTimeout(() => {
        pending = null
        last = Date.now()
        action()
      }, wait)
    }
  }
}
