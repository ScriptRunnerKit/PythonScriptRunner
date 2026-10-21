<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { safeReturn } from '@/router/paths'

/** Tela que não carregou nem depois de recarregar (spec 008, AC-27). Importada direto, sem depender da rede. */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

function reload() {
  window.location.assign(router.resolve(safeReturn(route.query.rota)).href)
}
</script>

<template>
  <v-container class="py-12 text-center">
    <v-icon icon="mdi-wifi-alert" size="64" color="warning" aria-hidden="true" />
    <h1 class="text-h5 mt-4">{{ t('loadFailed.title') }}</h1>
    <p class="text-medium-emphasis mt-2">{{ t('loadFailed.text') }}</p>
    <v-btn class="mt-6" color="primary" prepend-icon="mdi-refresh" data-testid="reload" @click="reload">{{
      t('loadFailed.reload')
    }}</v-btn>
  </v-container>
</template>
