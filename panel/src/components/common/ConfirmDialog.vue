<script setup lang="ts">
import { useI18n } from 'vue-i18n'

/** Confirmação de uma ação (cancelar, reprocessar, excluir, descartar). */
defineProps<{ title: string; text: string; confirm: string; color?: string; busy?: boolean }>()
const open = defineModel<boolean>({ required: true })
const emit = defineEmits<{ confirm: [] }>()
const { t } = useI18n()
</script>

<template>
  <v-dialog v-model="open" max-width="480" :aria-label="title">
    <v-card :title="title">
      <v-card-text>{{ text }}</v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="open = false">{{ t('common.cancel') }}</v-btn>
        <v-btn :color="color ?? 'primary'" :loading="busy" data-testid="confirm" @click="emit('confirm')">{{ confirm }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
