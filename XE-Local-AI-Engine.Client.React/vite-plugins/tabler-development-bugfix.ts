/** biome-ignore-all lint/suspicious/noConsole: <This is a Vite plugin, so we need to use console.log> */

import type { Plugin } from "vite";

/**
 * A Vite plugin to fix the Tabler development bug.
 * @returns {Plugin}
 */
export default function tablerDevelopmentBugfix(): Plugin {
	return {
		name: "vite-plugin-tabler-development-bugfix",
		enforce: "pre",
		config: (config, { command }) => {
			// The plugin should only run in development mode ("serve" command)
			if (command !== "serve" || !config.server) {
				console.log("Skipping Tabler development server bugfix plugin in non-development mode.");

				return null;
			}

			return {
				resolve: {
					alias: {
						// /esm/icons/index.mjs only exports the icons statically, so no separate chunks are created -- https://github.com/tabler/tabler-icons/issues/1233#issuecomment-2428245119
						"@tabler/icons-react": "@tabler/icons-react/dist/esm/icons/index.mjs",
					},
				},
			};
		},
	};
}
