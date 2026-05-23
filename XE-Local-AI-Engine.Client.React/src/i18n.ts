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
	});
