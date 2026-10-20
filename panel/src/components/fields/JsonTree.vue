<script setup lang="ts">
import { computed, ref } from 'vue'

/** Árvore expansível de um valor JSON (saídas `json`; spec 008). */
const props = withDefaults(defineProps<{ value: unknown; name?: string; depth?: number }>(), { name: undefined, depth: 0 })
const open = ref(props.depth < 1)
const isObject = computed(() => props.value !== null && typeof props.value === 'object')
const entries = computed(() =>
  isObject.value ? Object.entries(props.value as Record<string, unknown>) : ([] as [string, unknown][]),
)
const summary = computed(() =>
  Array.isArray(props.value) ? `[${(props.value as unknown[]).length}]` : `{${entries.value.length}}`,
)
const leaf = computed(() => (typeof props.value === 'string' ? JSON.stringify(props.value) : String(props.value)))
</script>

<template>
  <div class="json-tree" :style="{ paddingLeft: depth > 0 ? '16px' : '0' }">
    <template v-if="isObject">
      <button type="button" class="json-tree__toggle" :aria-expanded="open" @click="open = !open">
        <v-icon :icon="open ? 'mdi-chevron-down' : 'mdi-chevron-right'" size="16" aria-hidden="true" />
        <span v-if="name !== undefined" class="json-tree__key">{{ name }}:</span>
        <span class="text-medium-emphasis">{{ summary }}</span>
      </button>
      <template v-if="open">
        <JsonTree v-for="[key, child] in entries" :key="key" :name="key" :value="child" :depth="depth + 1" />
      </template>
    </template>
    <div v-else class="json-tree__leaf">
      <span v-if="name !== undefined" class="json-tree__key">{{ name }}:</span>
      <span>{{ leaf }}</span>
    </div>
  </div>
</template>

<style scoped>
.json-tree {
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 13px;
}
.json-tree__toggle {
  background: none;
  border: 0;
  color: inherit;
  cursor: pointer;
  padding: 0;
  font: inherit;
}
.json-tree__key {
  font-weight: 600;
  margin-right: 4px;
}
.json-tree__leaf {
  padding-left: 16px;
  word-break: break-word;
}
</style>
