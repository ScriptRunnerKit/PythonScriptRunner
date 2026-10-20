// Orçamento do bundle inicial (spec 008): ≤ 500 KB gzip para os arquivos que index.html carrega.
// Monaco e Vue Flow ficam em chunks carregados sob demanda e não entram na conta.
import { readFile } from 'node:fs/promises'
import { gzipSync } from 'node:zlib'
import { fileURLToPath } from 'node:url'

const limit = 500 * 1024
const dist = fileURLToPath(new URL('../dist/', import.meta.url))
const html = await readFile(`${dist}index.html`, 'utf8')
const files = [...html.matchAll(/(?:src|href)="\.\/(assets\/[^"]+\.(?:js|css))"/g)].map((m) => m[1])
let total = 0
for (const file of new Set(files)) {
  const size = gzipSync(await readFile(`${dist}${file}`)).length
  total += size
  console.log(`${(size / 1024).toFixed(1).padStart(8)} KB  ${file}`)
}

console.log(`${(total / 1024).toFixed(1).padStart(8)} KB  inicial (gzip), limite ${limit / 1024} KB`)
if (total > limit) {
  console.error('O bundle inicial passou do orçamento da spec 008.')
  process.exit(1)
}
