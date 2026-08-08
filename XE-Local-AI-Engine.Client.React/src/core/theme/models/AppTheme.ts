export interface AppTheme {
	palette: {
		mode: "light" | "dark";
		primary: {
			main: string;
		};
		secondary: {
			main: string;
		};
		divider: string;
		text: {
			primary: string;
			secondary: string;
		};
		background: {
			default: string;
			paper: string;
		};
		action: {
			selected: string;
			focus: string;
			hover: string;
			selectedOpacity: number;
		};
		grey: string[];
	};
	typography: {
		body2: {
			fontSize: string;
			fontWeight: number;
			lineHeight: number;
		};
	};
	shape: {
		borderRadius: number;
	};
	zIndex: {
		drawer: number;
	};
	shadows: string[];
	spacing: (factor: number) => string;
	transitions: {
		duration: {
			short: number;
		};
		create: (_properties: string[] | string, _options?: Record<string, unknown>) => string;
	};
	breakpoints: {
		up: (value: number) => string;
		down: (value: string | number) => string;
	};
	applyStyles: (targetMode: "light" | "dark", styles: Record<string, unknown>) => Record<string, unknown>;
}

export type Theme = AppTheme;
