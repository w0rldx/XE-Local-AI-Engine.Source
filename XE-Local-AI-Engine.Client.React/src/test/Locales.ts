// Discovers every locale JSON under src/locales at test time so parity tests cover new languages
// automatically — add src/locales/<code>.json and it is checked against `en` with no test edit.
// `eager: true` inlines the modules so imports resolve synchronously for the describe-time loops.

const modules = import.meta.glob<{ default: Record<string, unknown> }>("../locales/*.json", {
	eager: true,
});

export interface DiscoveredLocale {
	code: string;
	resource: Record<string, unknown>;
}

const allLocales: DiscoveredLocale[] = Object.entries(modules)
	.map(([path, mod]) => ({
		code: path.replace(/^.*\/([^/]+)\.json$/, "$1"),
		resource: mod.default,
	}))
	.sort((a, b) => a.code.localeCompare(b.code));

// `en` is the source/fallback and the reference every other locale is compared against, so the
// parity loops iterate only the translations that must mirror it.
export const nonEnglishLocales: DiscoveredLocale[] = allLocales.filter((l) => l.code !== "en");
