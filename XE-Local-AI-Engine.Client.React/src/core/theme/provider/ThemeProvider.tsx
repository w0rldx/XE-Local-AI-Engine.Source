import { generateColors } from "@mantine/colors-generator";
import {
	ActionIcon,
	Alert,
	alpha,
	Badge,
	Button,
	Card,
	type CSSVariablesResolver,
	createTheme,
	defaultVariantColorsResolver,
	localStorageColorSchemeManager,
	MantineProvider,
	PasswordInput,
	Popover,
	parseThemeColor,
	Radio,
	RadioIndicator,
	ScrollArea,
	ScrollAreaAutosize,
	TableScrollContainer,
	type VariantColorsResolver,
} from "@mantine/core";
import { Notifications } from "@mantine/notifications";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { sourceThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";
import classes from "@/core/theme/provider/ThemeProvider.module.css";
import type { ThemeProviderProperties } from "@/core/theme/provider/ThemeProvider.types";
import { colorSchemeStorageKey, useThemeStore } from "@/core/theme/stores/ThemeStore";

// Mantine's light-mode `--mantine-color-dimmed` is gray-6 (#868e96), which is about 3.3:1 on white — under the
// 4.5:1 WCAG 1.4.3 floor, and it is the colour of the size="sm"/"xs" secondary text that carries real meaning on
// every page (subtitles, hints, table captions). gray-7 (#495057) is about 8.2:1 and is already the resting
// label colour of the navigation rail, so nothing looks new. In dark mode dark-2 (#828282) on the dark-6/7 surfaces is
// 3.5–4:1 and read as faint, so dimmed moves halfway to dark-1 (#9d9d9d, 5:1 or better): all the way to dark-1 would
// sit next to the dark-0 body text and lose the hierarchy. Disabled Button/ActionIcon labels were dark-3 (about 2.5:1
// on the dark-6 disabled fill) and looked like plain text, not a control; dark-2 (about 3.5:1) makes them legible
// while staying well below the dark-0 label of an enabled control. Fixed once here rather than at the call sites, which are everywhere.
const cssVariablesResolver: CSSVariablesResolver = () => ({
	variables: {},
	light: {
		"--mantine-color-dimmed": "var(--mantine-color-gray-7)",
	},
	dark: {
		"--mantine-color-dimmed": "color-mix(in srgb, var(--mantine-color-dark-1), var(--mantine-color-dark-2))",
		"--mantine-color-disabled-color": "var(--mantine-color-dark-2)",
	},
});

interface FloatingSizeState {
	readonly availableWidth: number;
	readonly availableHeight: number;
	readonly elements: { readonly floating: HTMLElement };
}

// Caps a floating dropdown to the room the viewport leaves on its side and scrolls it when its content is taller. The
// overflow is set only when needed: a permanent `overflow-y: auto` would clip the arrow of every small popover.
function capFloatingToViewport({ availableWidth, availableHeight, elements }: FloatingSizeState): void {
	const floating = elements.floating;
	const maxHeight = Math.max(0, availableHeight);
	floating.style.maxWidth = `${Math.max(0, availableWidth)}px`;
	floating.style.maxHeight = `${maxHeight}px`;
	floating.style.overflowY = floating.scrollHeight > maxHeight ? "auto" : "";
}

// One manager for the app's lifetime, on the key the store's one-shot migration writes to.
const colorSchemeManager = localStorageColorSchemeManager({ key: colorSchemeStorageKey });

export function ThemeProvider({ children }: ThemeProviderProperties) {
	// The theme object is rebuilt on every render (it always was), so the translated defaults below follow a
	// language switch: useTranslation re-renders this provider, which hands MantineProvider a fresh theme.
	const { t } = useTranslation();
	const accentColor = useThemeStore((state) => state.accentColor);
	const accentScale = useMemo(() => (accentColor === null ? null : generateColors(accentColor)), [accentColor]);
	const themeConfiguration = sourceThemeConfiguration;
	const primaryScale = themeConfiguration.palette.primary.scale;
	const secondaryScale = themeConfiguration.palette.secondary.scale;
	const primaryMain = themeConfiguration.palette.primary.main;
	const secondaryMain = themeConfiguration.palette.secondary.main;
	const secondaryOutline = themeConfiguration.palette.secondary.outline;
	const breakpoints = themeConfiguration.breakpoints.values;

	const variantColorResolver: VariantColorsResolver = (input) => {
		const defaultResolvedColors = defaultVariantColorsResolver(input);

		if (input.variant !== "outline") {
			return defaultResolvedColors;
		}

		const parsed = parseThemeColor({ color: input.color, theme: input.theme });
		if (!parsed.isThemeColor || parsed.color !== "secondary" || parsed.shade !== undefined) {
			return defaultResolvedColors;
		}

		return {
			...defaultResolvedColors,
			color: secondaryOutline,
			border: `1px solid ${secondaryOutline}`,
			hover: alpha(secondaryOutline, 0.05),
		};
	};

	const theme = createTheme({
		fontFamily: themeConfiguration.typography.fontFamily,
		primaryColor: "primary",
		variantColorResolver,
		components: {
			// App-wide section-card look. Call sites can still opt out (withBorder={false}) or pick another
			// radius/padding; the defaults just make the common case the consistent case.
			Card: Card.extend({
				defaultProps: {
					withBorder: true,
					radius: "md",
					padding: "lg",
				},
			}),
			// Every table in the app is wrapped in a Table.ScrollContainer, which renders a Mantine ScrollArea whose
			// `type` defaults to "hover" — the scrollbar is hidden until a pointer enters the area. A touch device
			// never fires pointerenter, so on a phone or tablet an overflowing table simply looked clipped: the
			// columns past the fold and the row-action buttons at the right edge were undiscoverable, with nothing on
			// screen hinting that the region scrolls at all (live-observed at 390/768/1024px across the app).
			//
			// "auto" is Mantine's "scrollbars visible only when content overflows (like CSS overflow: auto)", so the
			// bar appears for exactly the tables that need one, on every input device. "always" would also be visible
			// on touch but would paint a permanent bar under tables that fit, which is noise on the majority of them.
			// The theme key is `TableScrollContainer` (what the component passes to useProps), not "Table.ScrollContainer".
			TableScrollContainer: TableScrollContainer.extend({
				defaultProps: {
					scrollAreaProps: { type: "auto" },
				},
			}),
			// The same argument one level down, for every ScrollArea the app mounts directly: "hover" hides the bar
			// until a pointer enters, which never happens on touch, so an overflowing region looks like it simply
			// ends. "auto" shows a bar exactly when the content overflows, on every input device.
			//
			// 8 rather than Mantine's 12 because the default bar is wide enough to sit on top of trailing controls in
			// a narrow region (live-observed over the side nav's group chevrons), and "present" reserves the gutter
			// only while that bar is actually shown, so nothing is painted over and no space is wasted when it is not.
			//
			// ScrollArea and ScrollArea.Autosize read SEPARATE theme keys ("ScrollArea" / "ScrollAreaAutosize"), so
			// both have to be declared or the autosize variant silently keeps Mantine's defaults.
			ScrollArea: ScrollArea.extend({
				defaultProps: {
					type: "auto",
					scrollbarSize: 8,
					offsetScrollbars: "present",
				},
			}),
			ScrollAreaAutosize: ScrollAreaAutosize.extend({
				defaultProps: {
					type: "auto",
					scrollbarSize: 8,
					offsetScrollbars: "present",
				},
			}),
			// Every floating dropdown — Popover, Menu, and Select/Autocomplete/MultiSelect through Combobox — renders a
			// Popover, and none of them passes `middlewares` unless a call site does, so this one default reaches all of
			// them. Mantine's own default only flips and shifts: on a short screen (phone landscape) where neither side
			// has room, the dropdown kept its full height and ran past the top or bottom of the viewport. The size
			// middleware caps it to the space it has and lets it scroll. A call site passing its own `middlewares`
			// replaces the whole object, so it has to bring `size` itself.
			Popover: Popover.extend({
				defaultProps: {
					middlewares: { flip: true, shift: true, size: { padding: 8, apply: capFloatingToViewport } },
				},
			}),
			// Trailing controls never shrink; the reasoning lives beside the class in ThemeProvider.module.css.
			Button: Button.extend({ classNames: { root: classes["trailing-control"] } }),
			Badge: Badge.extend({ classNames: { root: classes["trailing-control"] } }),
			ActionIcon: ActionIcon.extend({ classNames: { root: classes["trailing-control"] } }),
			// A visible unchecked circle in dark mode, for the plain radio and the one inside a Radio.Card.
			Radio: Radio.extend({ classNames: { radio: classes["radio"] } }),
			RadioIndicator: RadioIndicator.extend({ classNames: { indicator: classes["radio-indicator"] } }),
			// Mantine's Alert close button is an icon-only button with no text, and `closeButtonLabel` is what gives
			// it an accessible name. Almost every `withCloseButton` call site omitted it, so a screen reader read a
			// nameless "button". Defaulting it here fixes all of them at once; a call site can still pass its own.
			Alert: Alert.extend({
				defaultProps: {
					closeButtonLabel: t("common.close"),
				},
			}),
			// Mantine hardcodes the English "Toggle password visibility" on the eye button, so a German operator heard
			// English. Defaulted here for every PasswordInput; a call site that passes its own props replaces it.
			PasswordInput: PasswordInput.extend({
				defaultProps: {
					visibilityToggleButtonProps: { "aria-label": t("common.togglePasswordVisibility") },
				},
			}),
		},
		colors: {
			primary: accentScale ?? [
				primaryScale[0] ?? primaryMain,
				primaryScale[1] ?? primaryMain,
				primaryScale[2] ?? primaryMain,
				primaryScale[3] ?? primaryMain,
				primaryScale[4] ?? primaryMain,
				primaryScale[5] ?? primaryMain,
				primaryScale[6] ?? primaryMain,
				primaryScale[7] ?? primaryMain,
				primaryScale[8] ?? primaryMain,
				primaryScale[9] ?? primaryMain,
			],
			secondary: [
				secondaryScale[0] ?? secondaryMain,
				secondaryScale[1] ?? secondaryMain,
				secondaryScale[2] ?? secondaryMain,
				secondaryScale[3] ?? secondaryMain,
				secondaryScale[4] ?? secondaryMain,
				secondaryScale[5] ?? secondaryMain,
				secondaryScale[6] ?? secondaryMain,
				secondaryScale[7] ?? secondaryMain,
				secondaryScale[8] ?? secondaryMain,
				secondaryScale[9] ?? secondaryMain,
			],
		},
		breakpoints: {
			xs: `${breakpoints.xs / 16}em`,
			sm: `${breakpoints.sm / 16}em`,
			md: `${breakpoints.md / 16}em`,
			lg: `${breakpoints.lg / 16}em`,
			xl: `${breakpoints.xl / 16}em`,
		},
	});

	return (
		<MantineProvider
			theme={theme}
			defaultColorScheme="auto"
			colorSchemeManager={colorSchemeManager}
			cssVariablesResolver={cssVariablesResolver}
		>
			<Notifications position="top-right" autoClose={5000} />
			{children}
		</MantineProvider>
	);
}
