import { Box, Group } from "@mantine/core";

import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";
import { ThemeModeToggle } from "@/core/theme/components/ThemeModeToggle/ThemeModeToggle";

/** Theme and language controls pinned top-right on screens rendered outside the authenticated app shell. */
export function StandaloneScreenControls() {
	return (
		<Box pos="absolute" top={16} right={16} style={{ zIndex: 10 }}>
			<Group gap="xs" wrap="nowrap">
				<ThemeModeToggle />
				<LanguageMenu />
			</Group>
		</Box>
	);
}
