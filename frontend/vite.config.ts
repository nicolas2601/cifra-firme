import { defineConfig } from "vite";

export default defineConfig({
  build: {
    rolldownOptions: {
      onLog(level, log, handler) {
        // The Query library also ships RSC entry points; this bundle only runs in the browser.
        if (log.code === "MODULE_LEVEL_DIRECTIVE" && log.id?.includes("/@tanstack/react-query/")) return;
        handler(level, log);
      }
    }
  }
});
