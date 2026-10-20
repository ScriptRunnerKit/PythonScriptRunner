// Gera src/api/schema.d.ts a partir do snapshot versionado do OpenAPI do painel (spec 008; D-137).
// Os caminhos perdem o prefixo /psr/api (o painel usa o prefixo lido de <base href>) e os números
// aparecem como números (a API sempre os escreve assim; o "ou string" vem só da leitura tolerante).
// Uso: npm run api        (gera)
//      npm run api:check  (falha se o arquivo gerado estiver desatualizado; usado no CI)
import { readFile, writeFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import openapiTS, { astToString } from 'openapi-typescript'

const snapshot = fileURLToPath(
  new URL('../../tests/PythonScriptRunner.Dashboard.Tests/Snapshots/openapi-v1.verified.json', import.meta.url),
)
const output = fileURLToPath(new URL('../src/api/schema.d.ts', import.meta.url))

const document = JSON.parse(await readFile(snapshot, 'utf8'))
document.paths = Object.fromEntries(Object.entries(document.paths).map(([path, item]) => [path.replace(/^\/psr\/api/, ''), item]))
plainNumbers(document)

const header = '// Gerado por scripts/generate-api.mjs a partir do snapshot do OpenAPI. Não edite à mão: rode `npm run api`.\n\n'
const generated = header + astToString(await openapiTS(document, { defaultNonNullable: false, emptyObjectsUnknown: true }))

if (process.argv.includes('--check')) {
  const current = await readFile(output, 'utf8').catch(() => '')
  if (current.replaceAll('\r\n', '\n') !== generated) {
    console.error('src/api/schema.d.ts está desatualizado: rode `npm run api` e faça commit.')
    process.exit(1)
  }
} else {
  await writeFile(output, generated)
}

function plainNumbers(node) {
  if (Array.isArray(node)) {
    node.forEach(plainNumbers)
    return
  }

  if (node === null || typeof node !== 'object') {
    return
  }

  if (Array.isArray(node.type) && node.type.includes('string')) {
    const numeric = node.type.filter((t) => t === 'integer' || t === 'number')
    if (numeric.length === 1) {
      node.type = node.type.includes('null') ? [numeric[0], 'null'] : numeric[0]
      delete node.pattern
    }
  }

  Object.values(node).forEach(plainNumbers)
}
