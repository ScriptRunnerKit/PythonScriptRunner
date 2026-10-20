<script setup lang="ts">
import { watch, watchEffect } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useTheme } from 'vuetify'
import SessionExpiredDialog from '@/components/auth/SessionExpiredDialog.vue'
import { useNotify } from '@/stores/notify'
import { useRealtime } from '@/stores/realtime'
import { useSession } from '@/stores/session'
import { useThemeChoice } from '@/stores/theme'

const { t } = useI18n()
const route = useRoute()
const theme = useTheme()
const themeChoice = useThemeChoice()
const notify = useNotify()
const session = useSession()
const realtime = useRealtime()

watchEffect(() => theme.change(themeChoice.resolved()))
watchEffect(() => {
  const title = route.meta.title ? t(route.meta.title) : ''
  document.title = title ? `${title} · PythonScriptRunner` : 'PythonScriptRunner'
})

// O hub abre depois que o usuário foi identificado (spec 007: o hub exige papel Leitor).
watch(
  () => session.identified && session.role !== null,
  (ready) => (ready ? realtime.start() : void realtime.stop()),
  { immediate: true },
)
</script>

<template>
  <v-app>
    <router-view />
    <SessionExpiredDialog />
    <v-snackbar
      :model-value="notify.current !== null"
      :color="notify.current?.kind"
      :timeout="notify.current?.kind === 'error' ? 8000 : 4000"
      location="bottom right"
      @update:model-value="(open: boolean) => !open && notify.next()"
    >
      {{ notify.current?.text }}
      <template #actions>
        <v-btn variant="text" :aria-label="t('common.close')" icon="mdi-close" @click="notify.next()" />
      </template>
    </v-snackbar>
  </v-app>
</template>
