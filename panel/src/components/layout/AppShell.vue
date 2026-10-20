<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useDisplay } from 'vuetify'
import type { Role } from '@/api/client'
import { useRealtime } from '@/stores/realtime'
import { useSession } from '@/stores/session'
import { useThemeChoice, type ThemeChoice } from '@/stores/theme'

const { t } = useI18n()
const session = useSession()
const realtime = useRealtime()
const themeChoice = useThemeChoice()
const { lgAndUp } = useDisplay()
const drawer = ref<boolean | null>(null)

const items: { to: string; icon: string; label: string; role: Role }[] = [
  { to: 'overview', icon: 'mdi-view-dashboard-outline', label: 'nav.overview', role: 'Reader' },
  { to: 'stages', icon: 'mdi-language-python', label: 'nav.stages', role: 'Reader' },
  { to: 'pipelines', icon: 'mdi-transit-connection-variant', label: 'nav.pipelines', role: 'Reader' },
  { to: 'runs', icon: 'mdi-play-circle-outline', label: 'nav.runs', role: 'Reader' },
  { to: 'schedules', icon: 'mdi-calendar-clock', label: 'nav.schedules', role: 'Reader' },
  { to: 'audit', icon: 'mdi-clipboard-text-clock-outline', label: 'nav.audit', role: 'Admin' },
  { to: 'admin', icon: 'mdi-cog-outline', label: 'nav.admin', role: 'Admin' },
]
const visible = computed(() => items.filter((item) => session.hasRole(item.role)))
const themes: { value: ThemeChoice; icon: string }[] = [
  { value: 'system', icon: 'mdi-theme-light-dark' },
  { value: 'light', icon: 'mdi-white-balance-sunny' },
  { value: 'dark', icon: 'mdi-weather-night' },
]
const themeIcon = computed(() => themes.find((x) => x.value === themeChoice.choice)?.icon ?? 'mdi-theme-light-dark')
</script>

<template>
  <v-navigation-drawer v-model="drawer" :permanent="lgAndUp" :temporary="!lgAndUp" width="240">
    <div class="d-flex align-center px-4 py-4">
      <v-icon icon="mdi-language-python" color="primary" size="28" class="mr-2" aria-hidden="true" />
      <span class="text-subtitle-1 font-weight-medium">PythonScriptRunner</span>
    </div>
    <v-divider />
    <v-list nav density="comfortable" :aria-label="t('nav.label')">
      <!-- Uma lista só pode ter itens: cada link fica num listitem (WCAG; spec 008, AC-15). -->
      <div v-for="item in visible" :key="item.to" role="listitem">
        <v-list-item :to="{ name: item.to }" :prepend-icon="item.icon" :title="t(item.label)" :exact="item.to === 'overview'" />
      </div>
    </v-list>
  </v-navigation-drawer>

  <v-app-bar flat border="b" density="comfortable">
    <v-app-bar-nav-icon v-if="!lgAndUp" :aria-label="t('nav.toggle')" @click="drawer = !drawer" />
    <v-spacer />
    <v-menu>
      <template #activator="{ props: menu }">
        <v-btn v-bind="menu" :icon="themeIcon" variant="text" :aria-label="t('theme.label')" data-testid="theme-menu" />
      </template>
      <v-list density="compact">
        <v-list-item
          v-for="option in themes"
          :key="option.value"
          :prepend-icon="option.icon"
          :title="t(`theme.${option.value}`)"
          :active="themeChoice.choice === option.value"
          :data-testid="`theme-${option.value}`"
          @click="themeChoice.set(option.value)"
        />
      </v-list>
    </v-menu>
    <v-menu>
      <template #activator="{ props: menu }">
        <v-btn v-bind="menu" variant="text" prepend-icon="mdi-account-circle" data-testid="user-menu">
          {{ session.me?.name }}
        </v-btn>
      </template>
      <v-list density="compact" min-width="220">
        <v-list-item :title="session.me?.name" :subtitle="session.role ? t(`roles.${session.role}`) : ''" />
        <v-divider v-if="session.canLogout" />
        <v-list-item
          v-if="session.canLogout"
          prepend-icon="mdi-logout"
          :title="t('auth.logout')"
          data-testid="logout"
          @click="session.logout()"
        />
      </v-list>
    </v-menu>
  </v-app-bar>

  <v-main>
    <v-alert
      v-if="realtime.degraded"
      type="warning"
      variant="tonal"
      density="compact"
      rounded="0"
      icon="mdi-lan-disconnect"
      role="status"
      data-testid="realtime-banner"
    >
      {{ t('realtime.unavailable') }}
    </v-alert>
    <router-view />
  </v-main>
</template>
