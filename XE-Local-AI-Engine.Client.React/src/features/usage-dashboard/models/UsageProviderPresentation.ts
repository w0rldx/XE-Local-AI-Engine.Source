import type { TFunction } from "i18next";

// Mantine color token per known provider, with a stable gray for anything unrecognised (including the backend's
// `unknown` catch-all). Used for the donut segments and provider badges so a provider reads the same colour everywhere.
const PROVIDER_COLORS: Record<string, string> = {
	local: "blue.6",
	ollama: "grape.6",
	codex: "teal.6",
	azure: "cyan.6",
	unknown: "gray.6",
};

const FALLBACK_COLOR = "gray.6";

export function providerColor(provider: string): string {
	return PROVIDER_COLORS[provider] ?? FALLBACK_COLOR;
}

// Human label for a provider. Known providers get a translated label; any other value (defensive — the backend only
// emits the known set) is shown verbatim so it is never silently dropped.
export function providerLabel(provider: string, t: TFunction): string {
	switch (provider) {
		case "local":
			return t("pages.usage.providers.local", "Local (llama.cpp)");
		case "ollama":
			return t("pages.usage.providers.ollama", "Ollama");
		case "codex":
			return t("pages.usage.providers.codex", "Codex");
		case "azure":
			return t("pages.usage.providers.azure", "Azure");
		case "unknown":
			return t("pages.usage.providers.unknown", "Unknown");
		default:
			return provider;
	}
}
