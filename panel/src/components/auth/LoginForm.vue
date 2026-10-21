<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { VTextField } from 'vuetify/components'
import { useSession } from '@/stores/session'

/** Login fixo (D-129): nome + senha; erro genérico (D-135) e contagem regressiva no 429 (D-146). */
const props = defineProps<{ lockedName?: string }>()
const emit = defineEmits<{ success: [] }>()

const { t } = useI18n()
const session = useSession()
const name = ref(props.lockedName ?? '')
const password = ref('')
const busy = ref(false)
const invalid = ref(false)
const wait = ref(0)
const nameField = ref<VTextField | null>(null)
let timer: ReturnType<typeof setInterval> | null = null

const canSubmit = computed(() => !busy.value && wait.value === 0 && name.value.trim() !== '' && password.value !== '')

function countdown(seconds: number) {
  wait.value = seconds
  if (timer) {
    clearInterval(timer)
  }

  timer = setInterval(() => {
    wait.value = Math.max(0, wait.value - 1)
    if (wait.value === 0 && timer) {
      clearInterval(timer)
      timer = null
    }
  }, 1000)
}

async function submit() {
  if (!canSubmit.value) {
    return
  }

  busy.value = true
  invalid.value = false
  try {
    const result = await session.login(name.value.trim(), password.value)
    if (result.ok) {
      password.value = ''
      emit('success')
    } else if (result.reason === 'throttled') {
      countdown(result.retryAfter)
    } else {
      invalid.value = true
      password.value = ''
    }
  } finally {
    busy.value = false
  }
}

// Na página o foco vai ao usuário já ao montar: o autofocus do Vuetify espera ~50 ms e tiraria o foco de quem já começou a
// digitar. No diálogo de sessão expirada (usuário travado) fica o autofocus da senha, que espera a transição do diálogo.
onMounted(() => {
  if (!props.lockedName) {
    nameField.value?.focus()
  }
})

onBeforeUnmount(() => timer && clearInterval(timer))
</script>

<template>
  <v-form @submit.prevent="submit">
    <v-alert v-if="invalid" type="error" variant="tonal" density="compact" class="mb-4" role="alert">
      {{ t('auth.login.invalid') }}
    </v-alert>
    <v-alert
      v-if="wait > 0"
      type="warning"
      variant="tonal"
      density="compact"
      class="mb-4"
      role="status"
      data-testid="login-throttled"
    >
      {{ t('auth.login.throttled', { seconds: wait }) }}
    </v-alert>
    <v-text-field
      ref="nameField"
      v-model="name"
      :label="t('auth.login.name')"
      autocomplete="username"
      :readonly="Boolean(lockedName)"
      prepend-inner-icon="mdi-account"
      name="name"
    />
    <v-text-field
      v-model="password"
      :label="t('auth.login.password')"
      type="password"
      autocomplete="current-password"
      prepend-inner-icon="mdi-lock"
      name="password"
      :autofocus="Boolean(lockedName)"
    />
    <v-btn type="submit" color="primary" block size="large" :loading="busy" :disabled="!canSubmit">
      {{ t('auth.login.submit') }}
    </v-btn>
  </v-form>
</template>
