import { Box, Group } from "@mantine/core";

import { isCompactViewport } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";
import { ThemeModeToggle } from "@/core/theme/components/ThemeModeToggle/ThemeModeToggle";

/**
 * Theme and language controls on screens rendered outside the authenticated app shell. Pinned top-right on roomy
 * viewports; on a compact one (phone width, or phone landscape) they sit in the flow as a right-aligned row above the
 * page, because pinned they covered the centred title.
 */
export function StandaloneScreenControls() {
	const { width, height } = useWindowDimensions();
	const inFlow = isCompactViewport(width, height);

	return (
		<Box
			pos={inFlow ? "static" : "absolute"}
			top={inFlow ? undefined : 16}
			right={inFlow ? undefined : 16}
			pt={inFlow ? "md" : undefined}
			px={inFlow ? "md" : undefined}
			style={{ zIndex: 10 }}
			data-testid="standalone-screen-controls"
			data-in-flow={inFlow}
		>
			<Group gap="xs" wrap="nowrap" justify="flex-end">
				<ThemeModeToggle />
				<LanguageMenu />
			</Group>
		</Box>
	);
}
