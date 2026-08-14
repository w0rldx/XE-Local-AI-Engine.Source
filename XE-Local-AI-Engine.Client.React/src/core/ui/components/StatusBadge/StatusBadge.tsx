import { Badge, Loader, type MantineColor } from "@mantine/core";
import type { ReactNode } from "react";

interface StatusBadgeProps {
	label: ReactNode;
	color: MantineColor;
	/** Non-terminal state: adds an inline spinner to the pill so the operator sees work is still happening. */
	inProgress?: boolean;
	"aria-label"?: string;
	"data-testid"?: string;
}

// Standard status pill. Features own the status -> colour mapping and delegate rendering here so every
// status reads the same across the app.
export function StatusBadge({
	label,
	color,
	inProgress = false,
	"aria-label": ariaLabel,
	"data-testid": testId,
}: StatusBadgeProps) {
	return (
		<Badge
			color={color}
			variant="light"
			leftSection={inProgress ? <Loader size={10} color={color} /> : undefined}
			aria-label={ariaLabel}
			data-testid={testId}
		>
			{label}
		</Badge>
	);
}
