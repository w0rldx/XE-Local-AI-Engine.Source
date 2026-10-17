import i18next from "i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import { initReactI18next } from "react-i18next";

// No locale is bundled into the entry chunk: each one is its own chunk, fetched on demand by `loadLocale` below and
// awaited by the render gate in Main.tsx. The fallback `en` used to be a static import, which put its ~364 KB raw
// into the boot chunk of every session; it now loads in parallel with the detected language before the first paint.
const lazyLocales: Record<string, () => Promise<{ default: unknown }>> = {
	de: () => import("./locales/de.json"),
	en: () => import("./locales/en.json"),
};

// In-flight or finished loads by base language. A second caller gets the SAME promise: `init` fires `languageChanged`
// synchronously, so the listener below starts the detected language's fetch before i18nReady asks for it, and a
// plain "already requested" early return let the first paint go ahead before the chunk had landed.
const requested = new Map<string, Promise<void>>();

// Fetches the chunk for `language` and merges it into the live i18next store. Resolves (never rejects) so a
// failed chunk fetch degrades to whatever did load rather than blocking the first paint.
function loadLocale(language: string | undefined): Promise<void> {
	const base = language?.split("-")[0];
	const load = base ? lazyLocales[base] : undefined;
	if (!base || !load || i18next.hasResourceBundle(base, "translation")) {
		return Promise.resolve();
	}

	const pending =
		requested.get(base) ??
		load().then(
			(bundle) => {
				i18next.addResourceBundle(base, "translation", bundle.default, true, true);
			},
			() => {
				// Allow a later languageChanged to retry the fetch.
				requested.delete(base);
			},
		);
	requested.set(base, pending);
	return pending;
}

// Keeps `<html lang>` on the language actually being rendered. index.html ships a static `lang="en"`, so a
// German session used to be announced to screen readers — and hyphenated, and offered to translation tools —
// as English. Guarded because this module is also loaded by node-environment tests, which have no document.
function syncDocumentLanguage(language: string | undefined): void {
	const resolved = language ?? i18next.resolvedLanguage ?? i18next.language;
	if (typeof document !== "undefined" && resolved) {
		document.documentElement.lang = resolved;
	}
}

// A language switch at runtime changes the active language BEFORE its bundle exists, so the chunk is fetched
// here and `bindI18nStore: "added"` (below) re-renders subscribers once it lands.
i18next.on("languageChanged", (language: string) => {
	syncDocumentLanguage(language);
	loadLocale(language).catch(() => undefined);
});

i18next
	.use(LanguageDetector)
	.use(initReactI18next)
	.init({
		// Empty but present: with no `resources` i18next would look for a backend to load from. Bundles arrive
		// through `addResourceBundle` in `loadLocale`.
		resources: {},
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

// `languageChanged` does not fire for the language `init` itself detected, so the initial value is set here.
syncDocumentLanguage(undefined);

// Resolves once the UI is renderable; rejects when the `en` fallback did not load, because then every `t()` would print
// its raw key. A failed detected-language chunk is not fatal: the UI renders in English. The boot error screen retries
// with a document reload, not a second call: browsers commonly cache a failed module import.
async function loadI18n(): Promise<void> {
	const language = i18next.language;
	await Promise.all([loadLocale("en"), loadLocale(language)]);
	if (!i18next.hasResourceBundle("en", "translation")) {
		throw new Error("The English translation bundle failed to load.");
	}
	// `init` ran with no bundle, so `resolvedLanguage` is unset until the language is applied again. The
	// `languageChanged` listener re-fetches a detected-language chunk that failed; wait for that attempt as well and
	// re-apply if it landed, so the first paint is not swapped a frame later.
	await i18next.changeLanguage(language);
	await loadLocale(language);
	const base = language?.split("-")[0];
	if (base && i18next.resolvedLanguage !== base && i18next.hasResourceBundle(base, "translation")) {
		await i18next.changeLanguage(language);
	}
}

// Main.tsx holds the first render on it (a load-error screen with a retry when it rejects); the vitest setup awaits it.
export const i18nReady: Promise<void> = loadI18n();
