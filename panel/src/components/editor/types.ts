/** Problema marcado no editor (ex.: erro de sintaxe com linha e coluna). */
export interface Marker {
  line: number
  column?: number
  message: string
}
