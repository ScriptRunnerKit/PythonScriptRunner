<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import { useSession } from '@/stores/session'
import ScheduleTable from './ScheduleTable.vue'

const { t } = useI18n()
const session = useSession()
const table = ref<InstanceType<typeof ScheduleTable> | null>(null)
</script>

<template>
  <v-container fluid>
    <PageHeader :title="t('schedules.title')" :subtitle="t('schedules.subtitle')">
      <v-btn
        v-if="session.hasRole('Author')"
        color="primary"
        prepend-icon="mdi-plus"
        data-testid="create-schedule"
        @click="table?.create()"
      >
        {{ t('schedules.create') }}
      </v-btn>
    </PageHeader>
    <v-card><ScheduleTable ref="table" /></v-card>
  </v-container>
</template>
