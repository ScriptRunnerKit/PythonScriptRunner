<script setup lang="ts">
/** Cartão centralizado das páginas fora do painel (entrar, sem acesso, sem papel). */
defineProps<{ title: string; icon: string }>()
</script>

<template>
  <v-main>
    <v-container class="fill-height d-flex align-center justify-center">
      <v-card width="420" max-width="100%" class="pa-6" elevation="3">
        <div class="d-flex align-center mb-4">
          <v-icon :icon="icon" color="primary" size="36" class="mr-3" aria-hidden="true" />
          <h1 class="text-h5">{{ title }}</h1>
        </div>
        <slot />
      </v-card>
    </v-container>
  </v-main>
</template>
