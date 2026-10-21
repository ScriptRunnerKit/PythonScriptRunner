/** Tela cujo código não baixou: recarrega uma vez e, se falhar de novo, avisa (spec 008, AC-27; D-163). */
const storageKey = 'psr.reload'
const window = 10_000

/** Falha ao baixar um pedaço do painel (import dinâmico ou CSS pré-carregado), nos textos do Chromium, Firefox e Safari. */
export function isLoadError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error)
  return /dynamically imported module|Importing a module script failed|Unable to preload CSS/i.test(message)
}

/**
 * Decide se recarrega a página para `path`: sim na primeira falha; não se já recarregou para a mesma rota há menos de
 * 10 s (sem laço) ou se o navegador não deixa guardar a marca.
 */
export function claimReload(path: string, now: number, storage: Pick<Storage, 'getItem' | 'setItem'> | null): boolean {
  if (!storage) {
    return false
  }

  try {
    const last = JSON.parse(storage.getItem(storageKey) ?? 'null') as { path: string; at: number } | null
    if (last && last.path === path && now - last.at < window) {
      return false
    }

    storage.setItem(storageKey, JSON.stringify({ path, at: now }))
    return true
  } catch {
    return false
  }
}

/** O `sessionStorage`, ou `null` quando o navegador o bloqueia. */
export function sessionStore(): Storage | null {
  try {
    return globalThis.sessionStorage ?? null
  } catch {
    return null
  }
}
