import { defineConfig } from "@hey-api/openapi-ts";

export default defineConfig({
	input: "./openapi/v1.json",
	output: {
		path: "./src/core/api/generated",
		postProcess: ["biome:format"],
	},
	plugins: [
		{
			name: "@hey-api/client-axios",
			runtimeConfigPath: "./src/core/api/Generated.runtime.ts",
		},
		"@hey-api/typescript",
		"@hey-api/sdk",
		{
			name: "@tanstack/react-query",
			queryOptions: true,
			queryKeys: true,
			mutationOptions: true,
		},
	],
});
