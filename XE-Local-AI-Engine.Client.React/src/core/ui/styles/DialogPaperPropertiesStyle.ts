import type { Theme } from "@/core/theme/models/AppTheme";

export const DialogPaperPropertiesStyle = (theme: Theme): Record<string, unknown> => ({
	width: "100%",
	maxWidth: "444px",
	[theme.breakpoints.up(768)]: {
		maxWidth: "768px",
	},
});
