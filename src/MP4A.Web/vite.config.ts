import tailwind from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig(({ command }) => ({
  base: './',
  plugins: [react(), tailwind(), {
    name: 'production-content-policy',
    transformIndexHtml(html) {
      return command === 'build'
        ? html.replace("script-src 'self' 'unsafe-inline'", "script-src 'self'").replace(' ws://127.0.0.1:*', '')
        : html
    },
  }],
}))
