<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { artifactUrl, type Schemas } from '@/api/client'
import { formatBytes, formatDateTime } from '@/utils/format'

/** Arquivo de uma execução: nome, tamanho, tipo e download; expirado fica desabilitado (spec 008). */
const props = defineProps<{ artifact: Schemas['ArtifactInfo'] }>()
const { t } = useI18n()
const expired = computed(
  () => Boolean(props.artifact.contentDeletedAt) || new Date(props.artifact.contentExpiresAt).getTime() <= Date.now(),
)
</script>

<template>
  <v-card variant="outlined" class="d-inline-flex align-center pa-2 ma-1" :disabled="expired" :data-artifact="artifact.id">
    <v-icon icon="mdi-file-outline" class="mr-2" aria-hidden="true" />
    <div class="mr-3">
      <div class="text-body-2 font-weight-medium">{{ artifact.fileName }}</div>
      <div class="text-caption text-medium-emphasis">
        {{ formatBytes(artifact.sizeBytes) }} · {{ artifact.contentType }}
        <template v-if="expired">
          ·
          {{ t('files.expired', { when: formatDateTime(artifact.contentDeletedAt ?? artifact.contentExpiresAt) }) }}</template
        >
      </div>
    </div>
    <v-btn
      v-if="!expired"
      :href="artifactUrl(artifact.id)"
      icon="mdi-download"
      variant="text"
      size="small"
      :aria-label="t('files.download', { name: artifact.fileName })"
      download
    />
  </v-card>
</template>
