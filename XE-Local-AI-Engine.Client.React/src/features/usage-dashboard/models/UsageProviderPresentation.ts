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

// Every operator-registered external endpoint is classified as `external:{connectionId}` — one usage-provider string
// per CONNECTION, which is what keeps the ledger's single-string contract while still telling two endpoints apart.
const EXTERNAL_PROVIDER_PREFIX = "external:";
const EXTERNAL_PROVIDER_COLOR = "indigo.6";

// The connection id inside an `external:{connectionId}` provider string, or null for any other provider.
export function externalUsageConnectionId(provider: string): string | null {
	return provider.startsWith(EXTERNAL_PROVIDER_PREFIX) ? provider.slice(EXTERNAL_PROVIDER_PREFIX.length) : null;
}

export function providerColor(provider: string): string {
	if (externalUsageConnectionId(provider) !== null) {
		return EXTERNAL_PROVIDER_COLOR;
	}
	return PROVIDER_COLORS[provider] ?? FALLBACK_COLOR;
}

/**
 * Human label for a provider. Known providers get a translated label; any other value (defensive — the backend only
 * emits the known set) is shown verbatim so it is never silently dropped.
 *
 * External connections are the one provider whose label is not knowable from the string alone: the ledger records the
 * connection ID, and the operator-facing NAME lives in the external-provider configuration. `connectionNames` carries
 * that lookup in from the caller — this module stays a pure formatter and never fetches. A connection that has since
 * been deleted is absent from the map and falls back to the bare "External", which is still true of the recorded run.
 */
export function providerLabel(provider: string, t: TFunction, connectionNames?: ReadonlyMap<string, string>): string {
	const connectionId = externalUsageConnectionId(provider);
	if (connectionId !== null) {
		const external = t("pages.usage.providers.external", "External");
		const name = connectionNames?.get(connectionId)?.trim();
		return name ? `${external} · ${name}` : external;
	}
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
