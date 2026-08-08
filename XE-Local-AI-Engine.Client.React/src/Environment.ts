/** biome-ignore-all lint/style/useNamingConvention: <!-- These are environment variables, so we want to use ALL_CAPS_SNAKE_CASE --> */
import { createEnv } from "@t3-oss/env-core";
import { z } from "zod";

export const environment = createEnv({
	clientPrefix: "VITE_",
	client: {
		// Falls back to the product name when the build did not set it (e.g. an isolated unit-test run with no .env,
		// or a clean-checkout `vite build`). A real .env / CI value still overrides it. Mirrors VITE_APP_VERSION below.
		VITE_APP_TITLE: z.string().min(1).default("XE Local AI Engine"),
		VITE_API_VERSION: z
			.string()
			.regex(/^v\d+$/, "VITE_API_VERSION must follow the pattern v<number>.")
			.default("v1"),
		// Injected at build time from Directory.Build.props (see vite.config.ts). Falls back to a dev sentinel when
		// the build did not set it (e.g. an isolated unit-test run without the Vite env).
		VITE_APP_VERSION: z.string().min(1).default("0.0.0-dev"),
	},
	runtimeEnv: import.meta.env,
});
