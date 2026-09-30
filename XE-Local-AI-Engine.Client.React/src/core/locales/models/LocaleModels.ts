export interface HeaderBarTitleState {
	selectedApplicationLanguage: string;
	actions: {
		changeLanguage: (language: string) => void;
	};
}
