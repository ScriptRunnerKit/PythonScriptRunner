import { fileURLToPath, URL } from 'node:url'
import vue from '@vitejs/plugin-vue'
import vuetify from 'vite-plugin-vuetify'
import { defineConfig } from 'vitest/config'

// Desenvolvimento: `npm run dev` com um DashboardHost em PSR_DASHBOARD_URL (padrão http://localhost:5080, prefixo /psr).
const dashboard = process.env.PSR_DASHBOARD_URL ?? 'http://localhost:5080'

export default defineConfig({
  // Caminhos relativos: o pacote injeta <base href> com o prefixo configurado (spec 007, AC-14).
  base: './',
  plugins: [vue(), vuetify({ autoImport: true })],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    chunkSizeWarningLimit: 4096,
  },
  worker: {
    format: 'es',
  },
  server: {
    proxy: {
      '/api': {
        target: dashboard,
        rewrite: (path) => `/psr${path}`,
        cookiePathRewrite: { '/psr': '/' },
      },
      '/hubs': {
        target: dashboard,
        ws: true,
        rewrite: (path) => `/psr${path}`,
        cookiePathRewrite: { '/psr': '/' },
      },
    },
  },
  test: {
    environment: 'happy-dom',
    setupFiles: ['src/test/setup.ts'],
    server: { deps: { inline: ['vuetify'] } },
    css: false,
  },
})
