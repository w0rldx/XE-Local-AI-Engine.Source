import type { TFunction } from "i18next";

interface ChannelCopy {
	/** The Select option text, which carries the "Recommended" hint. */
	readonly label: string;
	/** The bare noun, for the sentences a channel is interpolated into ("from Stable — Recommended" does not read). */
	readonly name: string;
	readonly description: string;
}

// Every key is a literal so scripts/CheckI18nDefaults.mjs can see it; a template key would be invisible to it.
export function channelCopy(t: TFunction): Record<string, ChannelCopy> {
	return {
		stable: {
			label: t("pages.about.appUpdate.channels.stable"),
			name: t("pages.about.appUpdate.channelNames.stable"),
			description: t("pages.about.appUpdate.channelDescriptions.stable"),
		},
		preview: {
			label: t("pages.about.appUpdate.channels.preview"),
			name: t("pages.about.appUpdate.channelNames.preview"),
			description: t("pages.about.appUpdate.channelDescriptions.preview"),
		},
		development: {
			label: t("pages.about.appUpdate.channels.development"),
			name: t("pages.about.appUpdate.channelNames.development"),
			description: t("pages.about.appUpdate.channelDescriptions.development"),
		},
	};
}

// The channel the node reports, rendered as the operator-facing noun. An unknown value falls back to the raw
// string: the server is the authority on which channels exist, so inventing a label or dropping it would be worse.
export function channelName(t: TFunction, channel: string): string {
	return channelCopy(t)[channel]?.name ?? channel;
}
