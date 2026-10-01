import { Crepe, CrepeFeature } from '@milkdown/crepe'
import '@milkdown/crepe/theme/common/style.css'
import '@milkdown/crepe/theme/frame.css'
import {
  type CmdKey,
  commandsCtx,
  editorViewCtx,
} from '@milkdown/kit/core'
import {
  listItemSchema,
  toggleEmphasisCommand,
  toggleInlineCodeCommand,
  toggleStrongCommand,
  wrapInBulletListCommand,
} from '@milkdown/kit/preset/commonmark'
import { toggleStrikethroughCommand } from '@milkdown/kit/preset/gfm'
import { liftListItem } from '@milkdown/kit/prose/schema-list'
import { getMarkdown, replaceAll } from '@milkdown/kit/utils'
import './lumi.css'
import { remarkUnderline, toggleUnderlineCommand, underlineSchema } from './underline'

type HostMessage =
  | { version: 1; type: 'loadDocument'; markdown: string; revision: number }
  | { version: 1; type: 'executeCommand'; command: EditorCommand }
  | { version: 1; type: 'focusEditor' }
  | { version: 1; type: 'setTheme'; theme: EditorTheme }

type EditorCommand = 'bold' | 'italic' | 'underline' | 'strikethrough' | 'bulletList' | 'taskList' | 'inlineCode'
type EditorTheme = { foreground: string; muted: string; accent: string; selection: string }

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: unknown): void
        addEventListener(type: 'message', listener: (event: MessageEvent<HostMessage>) => void): void
      }
    }
  }
}

const host = window.chrome?.webview
const root = document.querySelector<HTMLElement>('#editor')!
let applyingHostDocument = false
let isComposing = false
let revision = 0

const crepe = new Crepe({
  root,
  defaultValue: '',
  features: {
    [CrepeFeature.Toolbar]: false,
    [CrepeFeature.Latex]: false,
  },
})

crepe.editor
  .use(remarkUnderline)
  .use(underlineSchema)
  .use(toggleUnderlineCommand)

crepe.on((listener) => {
  listener.markdownUpdated((_ctx, markdown) => {
    if (applyingHostDocument || isComposing) return
    revision += 1
    host?.postMessage({ version: 1, type: 'contentChanged', markdown, revision })
  })
})

await crepe.create()

function call<T>(command: { key: CmdKey<T> }) {
  crepe.editor.action((ctx) => {
    ctx.get(commandsCtx).call(command.key)
    ctx.get(editorViewCtx).focus()
  })
}

function toggleTaskList() {
  crepe.editor.action((ctx) => {
    const view = ctx.get(editorViewCtx)
    const { state } = view
    let transaction = state.tr
    const positions = new Set<number>()

    state.doc.nodesBetween(state.selection.from, state.selection.to, (node, pos) => {
      if (node.type.name === 'list_item') positions.add(pos)
    })

    if (state.selection.empty) {
      for (let depth = state.selection.$from.depth; depth > 0; depth--) {
        if (state.selection.$from.node(depth).type.name === 'list_item') {
          positions.add(state.selection.$from.before(depth))
          break
        }
      }
    }

    if (positions.size > 0) {
      const shouldCreateTasks = [...positions].some((pos) => state.doc.nodeAt(pos)?.attrs.checked == null)
      for (const pos of positions) {
        const node = state.doc.nodeAt(pos)!
        transaction = transaction.setNodeMarkup(pos, undefined, { ...node.attrs, checked: shouldCreateTasks ? false : null })
      }
      view.dispatch(transaction)
    } else {
      ctx.get(commandsCtx).call(wrapInBulletListCommand.key)
      const nextState = view.state
      let nextTransaction = nextState.tr
      for (let depth = nextState.selection.$from.depth; depth > 0; depth--) {
        const node = nextState.selection.$from.node(depth)
        if (node.type.name === 'list_item') {
          const pos = nextState.selection.$from.before(depth)
          nextTransaction = nextTransaction.setNodeMarkup(pos, undefined, { ...node.attrs, checked: false })
          break
        }
      }
      if (nextTransaction.docChanged) view.dispatch(nextTransaction)
    }
    view.focus()
  })
}

function toggleBulletList() {
  crepe.editor.action((ctx) => {
    const view = ctx.get(editorViewCtx)
    const inBulletList = Array.from(
      { length: view.state.selection.$from.depth + 1 },
      (_, depth) => view.state.selection.$from.node(depth).type.name,
    ).includes('bullet_list')

    if (inBulletList) {
      liftListItem(listItemSchema.type(ctx))(view.state, view.dispatch)
    } else {
      ctx.get(commandsCtx).call(wrapInBulletListCommand.key)
    }
    view.focus()
  })
}

function execute(command: EditorCommand) {
  switch (command) {
    case 'bold': call(toggleStrongCommand); break
    case 'italic': call(toggleEmphasisCommand); break
    case 'strikethrough': call(toggleStrikethroughCommand); break
    case 'bulletList': toggleBulletList(); break
    case 'taskList': toggleTaskList(); break
    case 'inlineCode': call(toggleInlineCodeCommand); break
    case 'underline': call(toggleUnderlineCommand); break
  }
}

host?.addEventListener('message', (event) => {
  const message = event.data
  if (!message || message.version !== 1) return

  if (message.type === 'loadDocument') {
    applyingHostDocument = true
    revision = message.revision
    crepe.editor.action(replaceAll(message.markdown, true))
    queueMicrotask(() => { applyingHostDocument = false })
  } else if (message.type === 'executeCommand') {
    execute(message.command)
  } else if (message.type === 'focusEditor') {
    crepe.editor.action((ctx) => ctx.get(editorViewCtx).focus())
  } else if (message.type === 'setTheme') {
    const style = document.documentElement.style
    style.setProperty('--lumi-foreground', message.theme.foreground)
    style.setProperty('--lumi-muted', message.theme.muted)
    style.setProperty('--lumi-accent', message.theme.accent)
    style.setProperty('--lumi-selection', message.theme.selection)
  }
})

root.addEventListener('compositionstart', () => {
  isComposing = true
  host?.postMessage({ version: 1, type: 'compositionChanged', isComposing: true })
})
root.addEventListener('compositionend', () => {
  isComposing = false
  host?.postMessage({ version: 1, type: 'compositionChanged', isComposing: false })
  revision += 1
  host?.postMessage({
    version: 1,
    type: 'contentChanged',
    markdown: crepe.editor.action(getMarkdown()),
    revision,
  })
})

host?.postMessage({ version: 1, type: 'ready' })
