/** Destino interno seguro para "voltar" depois do login (sem redirecionamento aberto). */
export function safeReturn(value: unknown): string {
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : '/'
}
