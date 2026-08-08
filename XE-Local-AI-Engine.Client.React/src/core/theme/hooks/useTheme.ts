import { useThemeStore } from "@/core/theme/stores/ThemeStore";

export const useTheme = () => {
	const mode = useThemeStore((state) => state.mode);
	const toggleColorMode = useThemeStore((state) => state.toggleColorMode);

	return {
		mode,
		toggleColorMode,
	};
};
