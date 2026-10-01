import { defineConfig } from 'vite'

export default defineConfig({
  base: './',
  build: {
    outDir: '../src/LumiMemo.App/EditorAssets',
    emptyOutDir: true,
    assetsInlineLimit: 0,
  },
})
