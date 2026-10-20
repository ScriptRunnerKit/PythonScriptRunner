<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useTheme } from 'vuetify'
import type * as Monaco from 'monaco-editor/editor/editor.api'
import type { Marker } from './types'

/**
 * Editor Monaco (Python, JSON ou texto), carregado sob demanda; tema sincronizado com o painel e `Ctrl+S` para salvar
 * (spec 008). Enquanto o Monaco carrega, o texto aparece num campo simples.
 */
const props = withDefaults(
  defineProps<{
    language?: 'python' | 'json' | 'plaintext'
    readonly?: boolean
    height?: string
    label?: string
    markers?: Marker[]
  }>(),
  { language: 'plaintext', readonly: false, height: '320px', label: '', markers: () => [] },
)
const model = defineModel<string>({ required: true })
const emit = defineEmits<{ save: [] }>()

const { t } = useI18n()
const theme = useTheme()
const host = ref<HTMLDivElement | null>(null)
const ready = ref(false)
const editor = shallowRef<Monaco.editor.IStandaloneCodeEditor | null>(null)
let monaco: typeof Monaco | null = null

function applyMarkers() {
  const textModel = editor.value?.getModel()
  if (!monaco || !textModel) {
    return
  }

  monaco.editor.setModelMarkers(
    textModel,
    'psr',
    props.markers.map((m) => ({
      startLineNumber: m.line,
      startColumn: m.column ?? 1,
      endLineNumber: m.line,
      endColumn: (m.column ?? 1) + 1,
      message: m.message,
      severity: monaco!.MarkerSeverity.Error,
    })),
  )
}

onMounted(async () => {
  monaco = (await import('./monaco')).monaco
  if (!host.value) {
    return
  }

  editor.value = monaco.editor.create(host.value, {
    value: model.value,
    language: props.language,
    readOnly: props.readonly,
    automaticLayout: true,
    minimap: { enabled: false },
    fontSize: 14,
    scrollBeyondLastLine: false,
    theme: theme.current.value.dark ? 'vs-dark' : 'vs',
    ariaLabel: props.label,
    tabSize: 4,
  })
  editor.value.onDidChangeModelContent(() => {
    const value = editor.value?.getValue() ?? ''
    if (value !== model.value) {
      model.value = value
    }
  })
  editor.value.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => emit('save'))
  applyMarkers()
  ready.value = true
})

watch(model, (value) => {
  if (editor.value && editor.value.getValue() !== value) {
    editor.value.setValue(value)
  }
})
watch(
  () => theme.current.value.dark,
  (dark) => monaco?.editor.setTheme(dark ? 'vs-dark' : 'vs'),
)
watch(() => props.markers, applyMarkers, { deep: true })
watch(
  () => props.readonly,
  (readOnly) => editor.value?.updateOptions({ readOnly }),
)

onBeforeUnmount(() => editor.value?.dispose())
</script>

<template>
  <div class="code-editor" :style="{ height }">
    <div ref="host" class="code-editor__host" :class="{ 'code-editor__host--hidden': !ready }" data-testid="code-editor" />
    <v-textarea
      v-if="!ready"
      v-model="model"
      class="code-editor__fallback"
      :readonly="readonly"
      :aria-label="label || t('editor.code')"
      no-resize
      hide-details
      :rows="10"
    />
  </div>
</template>

<style scoped>
.code-editor {
  position: relative;
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 4px;
  overflow: hidden;
}
.code-editor__host {
  position: absolute;
  inset: 0;
}
.code-editor__host--hidden {
  visibility: hidden;
}
.code-editor__fallback {
  position: absolute;
  inset: 0;
}
</style>
