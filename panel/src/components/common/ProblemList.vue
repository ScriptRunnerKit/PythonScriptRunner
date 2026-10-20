<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { Schemas } from '@/api/client'

/** Erros e avisos de validação (códigos estáveis); clicar avisa o editor para focar o passo ou campo. */
defineProps<{ report: Schemas['ValidationReport'] | null }>()
const emit = defineEmits<{ select: [issue: Schemas['ValidationIssue']] }>()
const { t } = useI18n()
</script>

<template>
  <div v-if="report" data-testid="problems">
    <v-alert v-if="report.errors.length === 0 && report.warnings.length === 0" type="success" variant="tonal" density="compact">
      {{ t('validation.ok') }}
    </v-alert>
    <v-list v-else density="compact" class="py-0">
      <v-list-item
        v-for="(issue, i) in [
          ...report.errors.map((x) => ({ ...x, severity: 'error' })),
          ...report.warnings.map((x) => ({ ...x, severity: 'warning' })),
        ]"
        :key="i"
        :prepend-icon="issue.severity === 'error' ? 'mdi-alert-circle' : 'mdi-alert'"
        :base-color="issue.severity"
        :title="issue.message"
        :subtitle="[issue.code, issue.step, issue.field].filter(Boolean).join(' · ')"
        @click="emit('select', issue)"
      />
    </v-list>
  </div>
</template>
