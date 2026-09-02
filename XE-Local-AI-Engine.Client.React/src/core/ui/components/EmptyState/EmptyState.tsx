import { type MantineSize, Stack, Text } from "@mantine/core";
import type { ReactNode } from "react";

interface EmptyStateProps {
	message: ReactNode;
	/** Optional decoration above the message; by convention a dimmed Tabler icon. */
	icon?: ReactNode;
	action?: ReactNode;
	/** Message text size; matches the surrounding content's scale (e.g. "sm" under a compact table). */
	size?: MantineSize;
	"data-testid"?: string;
}

// Standard empty-state for lists and panels. Without an icon it renders as the app-wide inline dimmed
// text; with one it becomes a centered figure (icon, message, optional action).
export function EmptyState({ message, icon, action, size, "data-testid": testId }: EmptyStateProps) {
	if (icon === undefined && action === undefined) {
		return (
			<Text c="dimmed" size={size} data-testid={testId}>
				{message}
			</Text>
		);
	}

	return (
		<Stack gap="sm" align="center" py="md" data-testid={testId}>
			{icon}
			<Text c="dimmed" size={size} ta="center">
				{message}
			</Text>
			{action}
		</Stack>
	);
}
