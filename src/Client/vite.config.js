import { defineConfig } from 'vite'

export default defineConfig({
  // Vite 8 defaults to IPv6 loopback (::1) only. Node resolves a hostname
  // (even "localhost") to a single address and binds only that one, so
  // "localhost" here still came back IPv6-only on this machine - use the
  // literal IPv4 address to force it, verified below.
  server: { port: 5173, host: '127.0.0.1' }
})
