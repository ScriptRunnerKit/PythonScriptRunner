<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import LoginForm from './LoginForm.vue'
import { useSession } from '@/stores/session'

/**
 * Sessão expirada no meio do uso (D-138, D-151): login por cima da tela, só para o mesmo usuário; ao entrar, as chamadas
 * pendentes são refeitas e a tela segue como estava.
 */
const { t } = useI18n()
const session = useSession()
</script>

<template>
  <v-dialog :model-value="session.expired" persistent max-width="440" aria-labelledby="session-expired-title">
    <v-card>
      <v-card-title id="session-expired-title" class="text-h6">{{ t('auth.expired.title') }}</v-card-title>
      <v-card-text>
        <p class="mb-4">{{ t('auth.expired.text') }}</p>
        <LoginForm :locked-name="session.me?.name" />
      </v-card-text>
      <v-card-actions>
        <v-btn variant="text" @click="session.switchUser()">{{ t('auth.expired.switchUser') }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
