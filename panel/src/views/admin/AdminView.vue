<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, unwrap, type Schemas } from '@/api/client'
import PageHeader from '@/components/common/PageHeader.vue'
import { useApiAction } from '@/composables/useApiAction'
import { formatBytes, formatDuration, timeSpanMs } from '@/utils/format'

/** Administração (Admin; spec 008 §7): status de segredos (nunca valores) e configurações globais somente leitura. */
const { t } = useI18n()
const { run } = useApiAction()
const secrets = ref<Schemas['SecretStatus'][]>([])
const settings = ref<Schemas['SettingsResponse'] | null>(null)

onMounted(async () => {
  const [s, c] = await Promise.all([run(() => unwrap(api.GET('/admin/secrets'))), run(() => unwrap(api.GET('/admin/settings')))])
  secrets.value = s ?? []
  settings.value = c ?? null
})
</script>

<template>
  <v-container fluid>
    <PageHeader :title="t('admin.title')" />
    <v-row>
      <v-col cols="12" lg="6">
        <v-card :title="t('admin.secrets')">
          <v-table density="comfortable" data-testid="secrets-table">
            <thead>
              <tr>
                <th>{{ t('admin.secretName') }}</th>
                <th>{{ t('admin.declaredBy') }}</th>
                <th>{{ t('admin.resolved') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="secret in secrets" :key="secret.name">
                <td>
                  <code>{{ secret.name }}</code>
                </td>
                <td>{{ secret.declaredBy.join(', ') }}</td>
                <td>
                  <v-icon
                    :icon="secret.resolved ? 'mdi-check-circle' : 'mdi-close-circle'"
                    :color="secret.resolved ? 'success' : 'error'"
                    :aria-label="secret.resolved ? t('admin.yesResolved') : t('admin.notResolved')"
                  />
                </td>
              </tr>
              <tr v-if="secrets.length === 0">
                <td colspan="3" class="text-medium-emphasis text-center py-4">{{ t('admin.noSecrets') }}</td>
              </tr>
            </tbody>
          </v-table>
        </v-card>
      </v-col>
      <v-col cols="12" lg="6">
        <v-card v-if="settings" :title="t('admin.settings')">
          <v-table density="compact">
            <tbody>
              <tr>
                <th>{{ t('admin.retentionArtifacts') }}</th>
                <td>{{ t('admin.days', { days: settings.retention.artifactDays }) }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.retentionRuns') }}</th>
                <td>{{ t('admin.days', { days: settings.retention.runDays }) }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.retentionAudit') }}</th>
                <td>{{ t('admin.days', { days: settings.retention.auditDays }) }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.maxFile') }}</th>
                <td>{{ formatBytes(settings.limits.maxFileBytes) }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.maxRunFiles') }}</th>
                <td>{{ formatBytes(settings.limits.maxRunFilesBytes) }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.maxLog') }}</th>
                <td>{{ formatBytes(settings.limits.maxLogBytes) }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.defaultTimeout') }}</th>
                <td>{{ formatDuration(timeSpanMs(settings.limits.defaultTimeout)) }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.defaultMemory') }}</th>
                <td>{{ settings.limits.defaultMemoryLimitMb }} MB</td>
              </tr>
              <tr>
                <th>{{ t('admin.processes') }}</th>
                <td>{{ settings.limits.maxConcurrentProcesses }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.python') }}</th>
                <td>{{ settings.limits.defaultPythonVersion }}</td>
              </tr>
              <tr>
                <th>{{ t('admin.access') }}</th>
                <td>{{ t(`accessMode.${settings.access.mode}`) }}</td>
              </tr>
              <tr v-for="(version, component) in settings.versions" :key="component">
                <th>{{ component }}</th>
                <td>{{ version }}</td>
              </tr>
            </tbody>
          </v-table>
        </v-card>
      </v-col>
    </v-row>
  </v-container>
</template>
