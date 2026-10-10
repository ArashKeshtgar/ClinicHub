import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// API on :5300 (src/ClinicHub.Api). Same origin through the proxy, so the cookie stays SameSite=Strict.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5301,
    strictPort: true,
    proxy: { '/api': 'http://localhost:5300' },
  },
})
