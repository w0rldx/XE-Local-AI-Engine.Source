import { Alert, type AlertProps, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { CSSProperties, ReactNode } from "react";

interface InlineErrorAlertProps extends Pick<AlertProps, "variant" | "mt" | "mb" | "my" | "p" | "w"> {
	/**
	 * The operator-facing text, ALREADY resolved. Call sites keep owning `apiErrorMessage(error, t(...))` — only a
	 * quarter of them resolve an error that way, the rest render a `t(...)` string, a state variable or a
	 * feature-local error mapper, and none of those would fit an `{ error, fallback }` prop pair.
	 */
	message: ReactNode;
	title?: ReactNode;
	/** Extra content under the message; by convention a retry button. Renders the pair as a stack. */
	children?: ReactNode;
	/** Replaces the default warning triangle. For the few banners that deliberately lead with another glyph. */
	icon?: ReactNode;
	/** DOM id, for the call sites that point an `aria-describedby` at the banner. */
	id?: string;
	style?: CSSProperties;
	"data-testid"?: string;
}

// Standard inline error banner: the red Alert with a warning triangle that every feature renders for a failed
// load, a failed mutation or a rejected submit. Features own the message; this owns the colour, icon and layout
// so one failure reads the same wherever it surfaces.
export function InlineErrorAlert({
	message,
	title,
	children,
	icon,
	id,
	style,
	variant,
	mt,
	mb,
	my,
	p,
	w,
	"data-testid": testId,
}: InlineErrorAlertProps) {
	return (
		<Alert
			color="red"
			icon={icon ?? <IconAlertTriangle size={16} />}
			title={title}
			id={id}
			style={style}
			variant={variant}
			mt={mt}
			mb={mb}
			my={my}
			p={p}
			w={w}
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
