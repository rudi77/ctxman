import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

// Dev-Proxy: die UI spricht relativ "/api/..."; Vite leitet an die ctxman-API weiter.
// Damit gibt es im Dev-Betrieb kein CORS-Problem (die API setzt keine CORS-Header).
// Ziel über VITE_API_TARGET konfigurierbar (Default: launchSettings-Port der Ctxman.Api).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  const target = env.VITE_API_TARGET || "http://localhost:5291";
  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        "/api": {
          target,
          changeOrigin: true,
          rewrite: (path) => path.replace(/^\/api/, ""),
        },
      },
    },
  };
});
