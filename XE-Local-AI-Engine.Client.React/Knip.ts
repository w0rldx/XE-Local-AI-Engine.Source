import type { KnipConfig } from "knip";

const config: KnipConfig = {
	entry: [
		"src/Main.tsx",
		"src/**/*.test.{ts,tsx}",
		"OpenapiTs.config.ts",
		"vite.config.ts",
		"vite-plugins/**/*.ts",
		"scripts/**/*.mjs",
	],
	project: ["src/**/*.{ts,tsx}", "vite-plugins/**/*.ts", "scripts/**/*.mjs"],
	ignore: ["src/core/api/generated/**"],
	ignoreDependencies: ["@hey-api/client-axios", "@tanstack/react-form", "@tanstack/react-pacer"],
	rules: {
		types: "off",
	},
	vite: false,
};

export default config;
