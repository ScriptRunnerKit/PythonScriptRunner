<script setup lang="ts">
/** Título da tela com ações à direita. */
defineProps<{ title: string; subtitle?: string }>()
</script>

<template>
  <div class="d-flex flex-wrap align-center ga-2 mb-4">
    <div class="mr-auto">
      <h1 class="text-h5">{{ title }}</h1>
      <div v-if="subtitle" class="text-medium-emphasis text-body-2">{{ subtitle }}</div>
      <slot name="subtitle" />
    </div>
    <slot />
  </div>
</template>
