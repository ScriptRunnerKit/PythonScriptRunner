<script setup lang="ts">
import { useI18n } from 'vue-i18n'

/** Rota acima do papel do usuário (spec 008, AC-10). */
const { t } = useI18n()
</script>

<template>
  <v-container class="py-12 text-center">
    <v-icon icon="mdi-lock-outline" size="64" color="warning" aria-hidden="true" />
    <h1 class="text-h5 mt-4">{{ t('auth.forbidden.title') }}</h1>
    <p class="text-medium-emphasis mt-2">{{ t('auth.forbidden.text') }}</p>
    <v-btn class="mt-6" color="primary" :to="{ name: 'overview' }">{{ t('nav.overview') }}</v-btn>
  </v-container>
</template>
