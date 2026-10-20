import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { hubUrl, type Schemas } from '@/api/client'

/** `RunUpdated` do hub (spec 007): a execução, o evento que a mudou e o passo atual. */
export interface RunUpdate {
  run: Schemas['RunSummary']
  eventType: string
  currentStep: string | null
}

/** `StepUpdated` do hub (spec 007). */
export interface StepUpdate {
  runId: string
  stepKey: string
  order: number
  status: Schemas['StepStatus']
  attempt: number
  startedAt: string | null
  finishedAt: string | null
  errorKind: Schemas['ErrorKind'] | null
}

export type Subscription =
  { kind: 'run'; id: string } | { kind: 'pipeline'; key: string } | { kind: 'stage'; key: string } | { kind: 'all' }

export interface LiveHandlers {
  onRun?: (update: RunUpdate) => void
  onStep?: (update: StepUpdate) => void
  /** Chamado a cada 5 s enquanto o tempo real está indisponível (spec 008, casos de borda). */
  poll?: () => void
}

const pollIntervalMs = 5000

function method(subscription: Subscription, subscribe: boolean): [string, string | undefined] {
  const verb = subscribe ? 'Subscribe' : 'Unsubscribe'
  switch (subscription.kind) {
    case 'run':
      return [`${verb}Run`, subscription.id]
    case 'pipeline':
      return [`${verb}Pipeline`, subscription.key]
    case 'stage':
      return [`${verb}Stage`, subscription.key]
    default:
      return [`${verb}All`, undefined]
  }
}

function groupOf(subscription: Subscription): string {
  return subscription.kind === 'all' ? 'all' : `${subscription.kind}:${'id' in subscription ? subscription.id : subscription.key}`
}

/**
 * Hub de execuções (spec 007; D-35): reconexão automática com espera crescente, reinscrição após reconectar e
 * *polling* de 5 s enquanto desconectado, com banner (spec 008).
 */
export const useRealtime = defineStore('realtime', () => {
  const state = ref<HubConnectionState>(HubConnectionState.Disconnected)
  const started = ref(false)
  const groups = new Map<string, { subscription: Subscription; count: number }>()
  const listeners = new Set<LiveHandlers>()
  let connection: HubConnection | null = null
  let pollTimer: ReturnType<typeof setInterval> | null = null
  let restartTimer: ReturnType<typeof setTimeout> | null = null
  let attempts = 0

  const degraded = computed(() => started.value && state.value !== HubConnectionState.Connected)

  function updatePolling() {
    if (degraded.value && pollTimer === null) {
      pollTimer = setInterval(() => listeners.forEach((l) => l.poll?.()), pollIntervalMs)
    } else if (!degraded.value && pollTimer !== null) {
      clearInterval(pollTimer)
      pollTimer = null
    }
  }

  function setState(value: HubConnectionState) {
    state.value = value
    updatePolling()
  }

  async function resubscribe() {
    for (const { subscription } of groups.values()) {
      const [name, arg] = method(subscription, true)
      await (arg === undefined ? connection?.invoke(name) : connection?.invoke(name, arg))
    }
  }

  function delay(count: number) {
    return Math.min(30_000, 1000 * 2 ** Math.min(count, 5))
  }

  async function connect() {
    if (!connection) {
      return
    }

    try {
      setState(HubConnectionState.Connecting)
      await connection.start()
      attempts = 0
      setState(HubConnectionState.Connected)
      await resubscribe()
      // Atualiza o que mudou enquanto a conexão estava fora.
      listeners.forEach((l) => l.poll?.())
    } catch {
      setState(HubConnectionState.Disconnected)
      restartTimer = setTimeout(() => void connect(), delay(attempts++))
    }
  }

  /** Abre a conexão (depois que o usuário foi identificado). */
  function start() {
    if (started.value) {
      return
    }

    started.value = true
    connection = new HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => delay(context.previousRetryCount) })
      .configureLogging(LogLevel.None)
      .build()
    connection.on('RunUpdated', (update: RunUpdate) => listeners.forEach((l) => l.onRun?.(update)))
    connection.on('StepUpdated', (update: StepUpdate) => listeners.forEach((l) => l.onStep?.(update)))
    connection.onreconnecting(() => setState(HubConnectionState.Reconnecting))
    connection.onreconnected(async () => {
      setState(HubConnectionState.Connected)
      await resubscribe()
      listeners.forEach((l) => l.poll?.())
    })
    connection.onclose(() => {
      setState(HubConnectionState.Disconnected)
      if (started.value) {
        restartTimer = setTimeout(() => void connect(), delay(attempts++))
      }
    })
    void connect()
  }

  async function stop() {
    started.value = false
    if (restartTimer) {
      clearTimeout(restartTimer)
    }

    await connection?.stop()
    connection = null
    setState(HubConnectionState.Disconnected)
  }

  /** Acompanha grupos do hub enquanto a tela estiver aberta; devolve a função que encerra. */
  function watch(subscriptions: Subscription[], handlers: LiveHandlers): () => void {
    listeners.add(handlers)
    for (const subscription of subscriptions) {
      const group = groupOf(subscription)
      const entry = groups.get(group)
      if (entry) {
        entry.count++
        continue
      }

      groups.set(group, { subscription, count: 1 })
      if (connection?.state === HubConnectionState.Connected) {
        const [name, arg] = method(subscription, true)
        void (arg === undefined ? connection.invoke(name) : connection.invoke(name, arg)).catch(() => {})
      }
    }

    updatePolling()
    return () => {
      listeners.delete(handlers)
      for (const subscription of subscriptions) {
        const group = groupOf(subscription)
        const entry = groups.get(group)
        if (!entry || --entry.count > 0) {
          continue
        }

        groups.delete(group)
        if (connection?.state === HubConnectionState.Connected) {
          const [name, arg] = method(subscription, false)
          void (arg === undefined ? connection.invoke(name) : connection.invoke(name, arg)).catch(() => {})
        }
      }
    }
  }

  return { state, started, degraded, start, stop, watch }
})
