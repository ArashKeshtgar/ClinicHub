import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// API on :5300 (src/ClinicHub.Api). Same origin through the proxy, so the sign-in cookie and the OpenID Connect
// endpoints (/connect, /.well-known) share the page's origin and the issuer is http://localhost:5301.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5301,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5300',
      // Keep the browser's Host so the sign-in server advertises its endpoints on :5301, where its pages are.
      '/connect': { target: 'http://localhost:5300', changeOrigin: false },
      '/.well-known': { target: 'http://localhost:5300', changeOrigin: false },
      // Results from LabFlow and ClinicReport (FHIR DiagnosticReport).
      '/fhir': { target: 'http://localhost:5300', changeOrigin: false },
    },
  },
})
