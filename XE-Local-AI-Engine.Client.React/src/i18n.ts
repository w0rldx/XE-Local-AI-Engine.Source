import i18next from "i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import { initReactI18next } from "react-i18next";

import translationGerman from "./locales/de.json";
import translationEnglish from "./locales/en.json";

const resources = {
	en: {
		translation: translationEnglish,
	},
	de: {
		translation: translationGerman,
	},
};

i18next
	.use(LanguageDetector)
	.use(initReactI18next)
	.init({
		resources,
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
	});
