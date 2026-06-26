// Client-side answer-language heuristic (locked decision D2 / plan §15.1). The backend does NOT tag answers with a
// `contentLanguage`, so the voice runtime needs a cheap local guess to route synthesis: "en" → Kokoro (WebGPU/WASM),
// "de"/other → Web Speech (Kokoro ships no German voice). This is deliberately small and conservative: it only
// distinguishes German from English, defaulting to English when there is no positive German signal.

export type AnswerLanguage = "en" | "de";

// German-only letters. Any of these is a strong, near-unambiguous German signal in this en/de-only context.
const GERMAN_DIACRITICS = /[äöüß]/i;

// Frequent German function words. Matched as whole words (case-insensitive) so English text that merely contains the
// letters (e.g. "under", "die-cut") does not trip the German branch. A small count threshold avoids a single loanword
// ("über", "die Hard") flipping an otherwise-English answer.
const GERMAN_STOPWORDS = [
	"der",
	"die",
	"das",
	"und",
	"nicht",
	"ich",
	"ist",
	"ein",
	"eine",
	"mit",
	"auf",
	"für",
	"sich",
	"dass",
	"oder",
	"auch",
	"werden",
	"wird",
	"kann",
];

const GERMAN_STOPWORD_PATTERN = new RegExp(`\\b(?:${GERMAN_STOPWORDS.join("|")})\\b`, "gi");

const GERMAN_STOPWORD_THRESHOLD = 2;

/**
 * Returns the best-guess language for a chunk of assistant answer text. German when it contains an umlaut/ß or at
 * least {@link GERMAN_STOPWORD_THRESHOLD} German stopwords; English otherwise (the safe default for empty/ambiguous
 * input). Pure + allocation-light so it can run on every sentence flush.
 */
export function detectAnswerLanguage(text: string): AnswerLanguage {
	if (GERMAN_DIACRITICS.test(text)) {
		return "de";
	}

	const matches = text.match(GERMAN_STOPWORD_PATTERN);
	if (matches && matches.length >= GERMAN_STOPWORD_THRESHOLD) {
		return "de";
	}

	return "en";
}
