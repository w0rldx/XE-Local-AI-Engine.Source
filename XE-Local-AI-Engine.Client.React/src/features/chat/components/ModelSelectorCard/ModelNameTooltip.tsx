import { Tooltip } from "@mantine/core";
import type { ReactElement } from "react";

import type { ModelDisplay } from "@/features/chat/models/ModelDisplay";

// The identity a shortened name left out — friendly label, raw id, serving connection — on hover and on focus.
// Never on touch: on a phone the tap has to reach the control underneath, which is the only way to open the picker.
//
// DISABLED rather than unmounted when the name was not shortened (so the tooltip never just repeats the text it
// covers): the trigger sits inside `Popover.Target`, which clones its single child, and swapping that child between
// `<Tooltip><Paper/></Tooltip>` and a bare `<Paper/>` as the selection changes remounts the target out from under the
// popover — which left the trigger showing the model it had before.
//
// `always` keeps it on even then: the composer trigger can be cut short by an ellipsis on a narrow row, which only
// the browser knows about, so there the full name is offered unconditionally.
export function ModelNameTooltip({
	display,
	always = false,
	children,
}: {
	display: ModelDisplay;
	always?: boolean;
	children: ReactElement;
}): ReactElement {
	return (
		<Tooltip
			label={display.full}
			disabled={!always && display.full === display.primary}
			withinPortal={true}
			openDelay={350}
			multiline={true}
			maw={300}
			events={{ hover: true, focus: true, touch: false }}
		>
			{children}
		</Tooltip>
	);
}
