<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * Conflito de edição (D-50; spec 008, AC-9): alguém salvou antes. Mostra o que mudou e oferece Recarregar (descarta as
 * alterações locais) ou Sobrescrever (salva de novo sobre a versão atual).
 */
const props = defineProps<{ mine: unknown; current: unknown; author?: string | null; when?: string | null }>()
const open = defineModel<boolean>({ required: true })
const emit = defineEmits<{ reload: []; overwrite: [] }>()
const { t } = useI18n()

/** Campos de primeiro nível que diferem. */
const changes = computed(() => {
  const a = (props.mine ?? {}) as Record<string, unknown>
  const b = (props.current ?? {}) as Record<string, unknown>
  return [...new Set([...Object.keys(a), ...Object.keys(b)])].filter((key) => JSON.stringify(a[key]) !== JSON.stringify(b[key]))
})
</script>

<template>
  <v-dialog v-model="open" max-width="560" persistent :aria-label="t('conflict.title')">
    <v-card data-testid="conflict-dialog">
      <v-card-title class="text-h6">{{ t('conflict.title') }}</v-card-title>
      <v-card-text>
        <p class="mb-3">{{ t('conflict.text', { author: author ?? '—', when: when ?? '—' }) }}</p>
        <template v-if="changes.length > 0">
          <p class="text-medium-emphasis mb-1">{{ t('conflict.changed') }}</p>
          <v-chip v-for="field in changes" :key="field" size="small" class="mr-1 mb-1" label>{{ field }}</v-chip>
        </template>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" data-testid="conflict-reload" @click="emit('reload')">{{ t('conflict.reload') }}</v-btn>
        <v-btn color="warning" data-testid="conflict-overwrite" @click="emit('overwrite')">{{ t('conflict.overwrite') }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
