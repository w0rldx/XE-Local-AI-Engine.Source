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
		// Runtime response schemas (zod v4). Paired with @hey-api/sdk validator below so generated
		// SDK calls validate the response shape at the transport boundary.
		// dates.offset: emit `z.iso.datetime({ offset: true })` so RFC3339 timezone offsets
		// (the backend serializes DateTimeOffset as `…+00:00`) validate. Without it, the default
		// `z.iso.datetime()` accepts only bare-`Z` UTC and rejects every offset-bearing response.
		{ name: "zod", dates: { offset: true } },
		// validator: true makes each generated SDK fn run its zod response schema as the client
		// `responseValidator`. A thrown ZodError is remapped to ApiError centrally (see Generated.runtime.ts).
		{ name: "@hey-api/sdk", validator: true },
		{
			name: "@tanstack/react-query",
			queryOptions: true,
			queryKeys: true,
			mutationOptions: true,
		},
	],
});
