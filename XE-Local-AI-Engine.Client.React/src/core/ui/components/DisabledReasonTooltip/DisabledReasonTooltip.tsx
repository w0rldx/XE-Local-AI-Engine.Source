import { Tooltip, type TooltipProps } from "@mantine/core";
import type { ReactNode } from "react";

interface DisabledReasonTooltipProps extends Omit<TooltipProps, "children" | "events"> {
	/** True while the wrapped control is disabled; the wrapper then takes its tab stop so the reason stays reachable. */
	readonly controlDisabled: boolean;
	readonly children: ReactNode;
}

const HOVER_AND_FOCUS = { hover: true, focus: true, touch: false } as const;
// inline-flex: the wrapper hugs the control without the line-box gap an inline span adds in a toolbar row.
const WRAPPER_STYLE = { display: "inline-flex" } as const;

// A disabled button gets neither pointer events nor focus, so a tooltip saying WHY it is disabled would be mouse-only
// at best. The span carries the tooltip instead and joins the tab order only while the control cannot.
export function DisabledReasonTooltip({ controlDisabled, children, ...tooltipProps }: DisabledReasonTooltipProps) {
	const focusable = controlDisabled && tooltipProps.disabled !== true;
	return (
		<Tooltip {...tooltipProps} events={HOVER_AND_FOCUS}>
			<span tabIndex={focusable ? 0 : undefined} style={WRAPPER_STYLE}>
				{children}
			</span>
		</Tooltip>
	);
}
