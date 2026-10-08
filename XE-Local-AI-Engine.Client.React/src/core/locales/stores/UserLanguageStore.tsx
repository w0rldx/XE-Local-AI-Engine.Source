import { create } from "zustand";

import type { HeaderBarTitleState } from "@/core/locales/models/LocaleModels";

const getInitialLanguage = () => {
	if (typeof window === "undefined") {
		return "en";
	}

	try {
		return localStorage.getItem("i18nextLng") || "en";
	} catch {
		// A browser that blocks site data throws on access, and this runs while the module evaluates.
		return "en";
	}
};

export const useUserLanguageStore = create<HeaderBarTitleState>()((set) => ({
	selectedApplicationLanguage: getInitialLanguage(),
	actions: {
		changeLanguage: (language: string): void => {
			set(() => ({ selectedApplicationLanguage: language }));
		},
	},
}));
