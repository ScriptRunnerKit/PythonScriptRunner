<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import CenteredCard from '@/components/layout/CenteredCard.vue'
import { useSession } from '@/stores/session'

/** Filtro do host negou (ou acesso local fora da máquina): sem formulário de login (D-139). */
const { t } = useI18n()
const session = useSession()
const router = useRouter()

async function retry() {
  await session.load(true)
  if (session.identified) {
    await router.replace({ name: 'overview' })
  }
}
</script>

<template>
  <CenteredCard :title="t('auth.noAccess.title')" icon="mdi-shield-lock-outline">
    <p class="mb-6">{{ t('auth.noAccess.text') }}</p>
    <v-btn color="primary" block @click="retry">{{ t('auth.noAccess.retry') }}</v-btn>
  </CenteredCard>
</template>
