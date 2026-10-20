<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import CenteredCard from '@/components/layout/CenteredCard.vue'
import { useSession } from '@/stores/session'

/** Autenticado pelo host sem papel do painel (D-144). */
const { t } = useI18n()
const session = useSession()
</script>

<template>
  <CenteredCard :title="t('auth.noRole.title')" icon="mdi-account-alert-outline">
    <p class="mb-6">{{ t('auth.noRole.text', { name: session.me?.name ?? '' }) }}</p>
    <v-btn v-if="session.canLogout" color="primary" block data-testid="logout" @click="session.logout()">
      {{ t('auth.logout') }}
    </v-btn>
  </CenteredCard>
</template>
