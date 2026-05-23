interface ILanguageItem {
	id: number;
	icon?: string;
	text: string;
	value: string;
}

export const languageData: ILanguageItem[] = [
	{
		id: 1,
		icon: "🇬🇧",
		text: "English",
		value: "en",
	},
	{
		id: 2,
		icon: "🇩🇪",
		text: "Deutsch",
		value: "de",
	},
];
