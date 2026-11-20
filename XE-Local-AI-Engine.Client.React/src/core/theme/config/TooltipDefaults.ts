import type { TooltipProps } from "@mantine/core";

// Mantine opens a Tooltip on hover only, so a keyboard or touch user never saw what an icon-only control does. A call
// site that passes its own `events` replaces the whole object. ThemeProvider sets it as the app default; FieldInfo passes
// it explicitly so its card opens on focus and touch under any provider.
export const tooltipDefaultProps = { events: { hover: true, focus: true, touch: true } } satisfies Partial<TooltipProps>;
