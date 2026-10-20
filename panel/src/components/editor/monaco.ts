// Monaco carregado sob demanda (spec 008: fora do bundle inicial), com os workers empacotados localmente
// (CSP `worker-src 'self' blob:`). Só realce de Python + snippets do psrunner (QA-008-2) e JSON com validação.
import * as monaco from 'monaco-editor/editor/editor.api'
import 'monaco-editor/languages/definitions/python/register'
import 'monaco-editor/language/json/monaco.contribution'
import EditorWorker from 'monaco-editor/editor/editor.worker?worker'
import JsonWorker from 'monaco-editor/language/json/json.worker?worker'

declare global {
  interface Window {
    MonacoEnvironment?: monaco.Environment
  }
}

window.MonacoEnvironment = {
  getWorker: (_id: string, label: string) => (label === 'json' ? new JsonWorker() : new EditorWorker()),
}

const snippets: { label: string; documentation: string; insertText: string }[] = [
  {
    label: 'psrunner stage',
    documentation: 'Esqueleto de um Stage (@stage def run(inputs, ctx))',
    insertText: [
      'from psrunner import stage',
      '',
      '',
      '@stage',
      'def run(inputs, ctx):',
      '    ${1:valor} = inputs["${2:entrada}"]',
      '    return {"${3:saida}": ${1:valor}}',
      '',
    ].join('\n'),
  },
  { label: 'ctx.log.info', documentation: 'Log no stdout da tentativa', insertText: 'ctx.log.info("${1:mensagem}")' },
  {
    label: 'ctx.new_file',
    documentation: 'Arquivo de saída (devolva-o no dicionário de saídas)',
    insertText: '${1:arquivo} = ctx.new_file("${2:nome.txt}", "${3:text/plain}")',
  },
  { label: 'ctx.secrets', documentation: 'Segredo declarado no Stage', insertText: 'ctx.secrets["${1:NOME}"]' },
  {
    label: 'StageError',
    documentation: 'Falha de negócio com código estável',
    insertText: 'raise StageError("${1:mensagem}", code="${2:CODIGO}")',
  },
]

monaco.languages.registerCompletionItemProvider('python', {
  provideCompletionItems: (model, position) => {
    const word = model.getWordUntilPosition(position)
    const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn)
    return {
      suggestions: snippets.map((s) => ({
        label: s.label,
        kind: monaco.languages.CompletionItemKind.Snippet,
        documentation: s.documentation,
        insertText: s.insertText,
        insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
        range,
      })),
    }
  },
})

export { monaco }
