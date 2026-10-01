import { toggleMark } from '@milkdown/kit/prose/commands'
import { $command, $markSchema, $remark } from '@milkdown/kit/utils'

type AstNode = {
  type: string
  value?: string
  children?: AstNode[]
  [key: string]: unknown
}

function transformUnderline(node: AstNode) {
  if (!node.children) return

  for (const child of node.children) transformUnderline(child)

  const output: AstNode[] = []
  for (let index = 0; index < node.children.length; index++) {
    const child = node.children[index]!
    if (child.type !== 'html' || child.value?.trim().toLowerCase() !== '<u>') {
      output.push(child)
      continue
    }

    const closing = node.children.findIndex(
      (candidate, candidateIndex) =>
        candidateIndex > index
        && candidate.type === 'html'
        && candidate.value?.trim().toLowerCase() === '</u>',
    )
    if (closing < 0) {
      output.push(child)
      continue
    }

    output.push({ type: 'underline', children: node.children.slice(index + 1, closing) })
    index = closing
  }
  node.children = output
}

function remarkUnderlinePlugin(this: { data(): Record<string, unknown> }) {
  const data = this.data() as { toMarkdownExtensions?: unknown[] }
  const extensions = data.toMarkdownExtensions ?? (data.toMarkdownExtensions = [])
  extensions.push({
    handlers: {
      underline(node: AstNode, _parent: AstNode, state: any, info: any) {
        const exit = state.enter('underline')
        const content = state.containerPhrasing(node, { ...info, before: '<', after: '>' })
        exit()
        return `<u>${content}</u>`
      },
    },
  })

  return (tree: AstNode) => transformUnderline(tree)
}

export const remarkUnderline = $remark(
  'remarkUnderline',
  () => remarkUnderlinePlugin as never,
)

export const underlineSchema = $markSchema('underline', () => ({
  parseDOM: [
    { tag: 'u' },
    {
      style: 'text-decoration',
      getAttrs: (value: string) => value.includes('underline') ? null : false,
    },
  ],
  toDOM: () => ['u', 0],
  parseMarkdown: {
    match: (node) => node.type === 'underline',
    runner: (state, node, markType) => {
      state.openMark(markType)
      state.next(node.children)
      state.closeMark(markType)
    },
  },
  toMarkdown: {
    match: (mark) => mark.type.name === 'underline',
    runner: (state, mark) => {
      state.withMark(mark, 'underline')
    },
  },
}))

export const toggleUnderlineCommand = $command('ToggleUnderline', (ctx) => () =>
  toggleMark(underlineSchema.type(ctx)),
)
