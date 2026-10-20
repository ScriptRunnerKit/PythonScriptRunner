import type { InjectionKey, Ref } from 'vue'

/** Origem da ligação sendo arrastada no editor. */
export interface DragSource {
  nodeId: string
  handleId: string
}

/** Contexto do canvas para os nós (somente leitura no fluxo ao vivo). */
export interface FlowContext {
  editable: boolean
  dragging: Ref<DragSource | null>
  /** A entrada `handleId` do nó `nodeId` aceita a ligação que está sendo arrastada? (tipos da spec 003 + ordem linear; D-52) */
  accepts(nodeId: string, handleId: string): boolean
  /** Clique numa entrada (valor fixo ou ligação implícita). */
  inputClicked(nodeId: string, input: string): void
  /** Clique no nó (painel lateral). */
  nodeClicked(nodeId: string): void
}

export const flowContextKey: InjectionKey<FlowContext> = Symbol('psr-flow')
