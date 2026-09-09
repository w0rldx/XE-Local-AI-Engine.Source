import { Alert, type AlertProps, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { ReactNode } from "react";

interface InlineErrorAlertProps extends Pick<AlertProps, "variant" | "mt" | "mb" | "my"> {
	/**
	 * The operator-facing text, ALREADY resolved. Call sites keep owning `apiErrorMessage(error, t(...))` — only a
	 * quarter of them resolve an error that way, the rest render a `t(...)` string, a state variable or a
	 * feature-local error mapper, and none of those would fit an `{ error, fallback }` prop pair.
	 */
	message: ReactNode;
	title?: ReactNode;
	/** Extra content under the message; by convention a retry button. Renders the pair as a stack. */
	children?: ReactNode;
	"data-testid"?: string;
}

// Standard inline error banner: the red Alert with a warning triangle that every feature renders for a failed
// load, a failed mutation or a rejected submit. Features own the message; this owns the colour, icon and layout
// so one failure reads the same wherever it surfaces.
export function InlineErrorAlert({
	message,
	title,
	children,
	variant,
	mt,
	mb,
	my,
	"data-testid": testId,
}: InlineErrorAlertProps) {
	return (
		<Alert
			color="red"
			icon={<IconAlertTriangle size={16} />}
			title={title}
			variant={variant}
			mt={mt}
			mb={mb}
			my={my}
			data-testid={testId}
		>
			{children === undefined ? (
				message
			) : (
				<Stack gap="sm" align="flex-start">
					<Text size="sm">{message}</Text>
					{children}
				</Stack>
			)}
		</Alert>
	);
}
