<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import LoginForm from '@/components/auth/LoginForm.vue'
import CenteredCard from '@/components/layout/CenteredCard.vue'
import { safeReturn } from '@/router'

/** Página /entrar do login fixo (D-129, D-138): ao entrar, volta à rota pedida. */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

function entered() {
  void router.replace(safeReturn(route.query.volta))
}
</script>

<template>
  <CenteredCard :title="t('auth.login.title')" icon="mdi-language-python">
    <p class="text-medium-emphasis mb-6">{{ t('auth.login.subtitle') }}</p>
    <LoginForm @success="entered" />
  </CenteredCard>
</template>
