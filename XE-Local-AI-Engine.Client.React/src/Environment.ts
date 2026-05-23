/** biome-ignore-all lint/style/useNamingConvention: <!-- These are environment variables, so we want to use ALL_CAPS_SNAKE_CASE --> */
import { createEnv } from "@t3-oss/env-core";
import { z } from "zod";

export const environment = createEnv({
	clientPrefix: "VITE_",
	client: {
		VITE_APP_TITLE: z.string().min(1),
		VITE_API_URL: z.string().url(),
		VITE_API_VERSION: z
			.string()
			.regex(/^v\d+$/, "VITE_API_VERSION must follow the pattern v<number>.")
			.default("v1"),
	},
	runtimeEnv: import.meta.env,
});
