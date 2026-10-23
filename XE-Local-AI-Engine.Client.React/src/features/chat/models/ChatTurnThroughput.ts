import type { TFunction } from "i18next";

import type { ChatTurnThroughput } from "@/features/chat/models/NodeChatStreamTypes";

// One muted line for an assistant turn: "1.4 s to first token · 75 tok/s · 2,080 cached". A part whose numbers are
// missing is left out; undefined when no part remains. tok/s is the decode rate (generationTokens / generationMs),
// one decimal under 10 and an integer above.
export function turnThroughputLabel(
	throughput: ChatTurnThroughput | undefined,
	t: TFunction,
	language: string,
): string | undefined {
	if (!throughput) {
		return undefined;
	}

	const oneDecimal = new Intl.NumberFormat(language, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
	const integer = new Intl.NumberFormat(language, { maximumFractionDigits: 0 });
	const parts: string[] = [];
	const { timeToFirstTokenMs, generationTokens, generationMs, cachedPromptTokens } = throughput;
	if (timeToFirstTokenMs != null && timeToFirstTokenMs >= 0) {
		parts.push(
			t("pages.chat.turnThroughput.timeToFirstToken", "{{value}} s to first token", {
				value: oneDecimal.format(timeToFirstTokenMs / 1000),
			}),
		);
	}
	if (generationTokens != null && generationMs != null && generationTokens > 0 && generationMs > 0) {
		const tokensPerSecond = (generationTokens / generationMs) * 1000;
		parts.push(
			t("pages.chat.turnThroughput.tokensPerSecond", "{{value}} tok/s", {
				value: (tokensPerSecond < 10 ? oneDecimal : integer).format(tokensPerSecond),
			}),
		);
	}
	if (cachedPromptTokens != null && cachedPromptTokens > 0) {
		parts.push(t("pages.chat.turnThroughput.cached", "{{value}} cached", { value: integer.format(cachedPromptTokens) }));
	}

	return parts.length > 0 ? parts.join(" · ") : undefined;
}
