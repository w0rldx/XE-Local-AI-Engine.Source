import { defineConfig, presetWind3 } from "unocss";

export default defineConfig({
	// Declared explicitly so the preset is visible here instead of being an implicit default of `unocss/vite`.
	// This is exactly what unocss/vite passes as its default (`VitePlugin(config, { presets: [presetWind3()] })`),
	// so the generated CSS is unchanged. Do NOT swap to presetWind4 — it is a different utility surface.
	presets: [presetWind3()],
});
