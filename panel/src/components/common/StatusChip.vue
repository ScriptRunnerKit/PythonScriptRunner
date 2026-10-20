<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { statusColor, statusIcon } from '@/utils/format'

/** Status de execução, passo, tentativa ou versão com cor e ícone (o texto vem do i18n). */
const props = defineProps<{ status: string; kind?: 'run' | 'step' | 'attempt' | 'version' }>()
const { t, te } = useI18n()

function label() {
  const key = `status.${props.kind ?? 'run'}.${props.status}`
  return te(key) ? t(key) : props.status
}
</script>

<template>
  <v-chip
    :color="statusColor(status)"
    :prepend-icon="statusIcon(status)"
    size="small"
    variant="tonal"
    label
    :data-status="status"
  >
    {{ label() }}
  </v-chip>
</template>
