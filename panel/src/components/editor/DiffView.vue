<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useTheme } from 'vuetify'
import type * as Monaco from 'monaco-editor/editor/editor.api'

/** Diff entre duas versões (Monaco diff; spec 008). */
const props = withDefaults(defineProps<{ original: string; modified: string; language?: string; height?: string }>(), {
  language: 'python',
  height: '420px',
})

const theme = useTheme()
const host = ref<HTMLDivElement | null>(null)
const editor = shallowRef<Monaco.editor.IStandaloneDiffEditor | null>(null)
let monaco: typeof Monaco | null = null

function setModels() {
  if (!monaco || !editor.value) {
    return
  }

  const previous = editor.value.getModel()
  editor.value.setModel({
    original: monaco.editor.createModel(props.original, props.language),
    modified: monaco.editor.createModel(props.modified, props.language),
  })
  previous?.original.dispose()
  previous?.modified.dispose()
}

onMounted(async () => {
  monaco = (await import('./monaco')).monaco
  if (!host.value) {
    return
  }

  editor.value = monaco.editor.createDiffEditor(host.value, {
    readOnly: true,
    automaticLayout: true,
    renderSideBySide: true,
    minimap: { enabled: false },
    theme: theme.current.value.dark ? 'vs-dark' : 'vs',
  })
  setModels()
})

watch(() => [props.original, props.modified], setModels)
watch(
  () => theme.current.value.dark,
  (dark) => monaco?.editor.setTheme(dark ? 'vs-dark' : 'vs'),
)
onBeforeUnmount(() => {
  const models = editor.value?.getModel()
  editor.value?.dispose()
  models?.original.dispose()
  models?.modified.dispose()
})
</script>

<template>
  <div ref="host" class="diff-view" :style="{ height }" data-testid="diff-view" />
</template>

<style scoped>
.diff-view {
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 4px;
}
</style>
