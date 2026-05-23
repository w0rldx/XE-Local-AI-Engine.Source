import { create } from "zustand";

import type { HeaderBarTitleState } from "@/core/locales/models/LocaleModels";

const getInitialLanguage = () => {
	if (typeof window === "undefined") {
		return "en";
	}

	return localStorage.getItem("i18nextLng") || "en";
};

export const useUserLanguageStore = create<HeaderBarTitleState>()((set) => ({
	selectedApplicationLanguage: getInitialLanguage(),
	changeLanguage: (language: string): void => {
		set(() => ({ selectedApplicationLanguage: language }));
	},
}));
