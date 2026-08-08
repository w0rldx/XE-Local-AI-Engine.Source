import i18next from "i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import { initReactI18next } from "react-i18next";

import translationEnglish from "./locales/en.json";

// Only the fallback locale is bundled into the entry chunk. Importing every locale statically put BOTH
// translation files (~293 KB raw / ~79 KB gzip combined) into the initial payload of every session, so each
// user downloaded a full language they would never render. Non-fallback locales are split into their own
// chunks and fetched on demand — see `loadLocale` below and the render gate in Main.tsx.
const lazyLocales: Record<string, () => Promise<{ default: unknown }>> = {
	de: () => import("./locales/de.json"),
};

const requested = new Set<string>();

// Fetches the chunk for `language` and merges it into the live i18next store. Resolves (never rejects) so a
// failed chunk fetch degrades to the English fallback rather than blocking the first paint.
async function loadLocale(language: string | undefined): Promise<void> {
	const base = language?.split("-")[0];
	if (!base) {
		return;
	}

	const load = lazyLocales[base];
	if (!load || requested.has(base) || i18next.hasResourceBundle(base, "translation")) {
		return;
	}

	requested.add(base);
	try {
		const bundle = await load();
		i18next.addResourceBundle(base, "translation", bundle.default, true, true);
	} catch {
		// Allow a later languageChanged to retry the fetch.
		requested.delete(base);
	}
}

// A language switch at runtime changes the active language BEFORE its bundle exists, so the chunk is fetched
// here and `bindI18nStore: "added"` (below) re-renders subscribers once it lands.
i18next.on("languageChanged", (language: string) => {
	loadLocale(language).catch(() => undefined);
});

i18next
	.use(LanguageDetector)
	.use(initReactI18next)
	.init({
		resources: {
			en: {
				translation: translationEnglish,
			},
		},
		fallbackLng: "en",
		detection: {
			order: ["localStorage"],
			lookupLocalStorage: "i18nextLng",
		},
		// React already escapes text nodes, so i18next's own HTML escaping is redundant and corrupts
		// interpolated values — e.g. a model name like "hf.co/unsloth/…" turns the "/" into "&#x2F;",
		// which then prints literally in toasts. Disabling it is the standard react-i18next config and
		// stays XSS-safe because React performs the escaping at render time.
		interpolation: { escapeValue: false },
		// Re-render on resource-store writes, not just language switches: a lazily-loaded bundle arrives
		// after `languageChanged` has already fired, and without this the UI would sit on the English
		// fallback until some unrelated render happened to flush it.
		react: { bindI18nStore: "added" },
	});

// Resolves once the detected language is renderable. Already-resolved for "en" (statically bundled), so an
// English session paints exactly as before; a non-English session waits on one same-origin chunk instead of
// painting English and swapping it a frame later.
export const i18nReady: Promise<void> = loadLocale(i18next.resolvedLanguage ?? i18next.language);
