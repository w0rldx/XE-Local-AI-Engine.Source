import type { CSSProperties, ReactNode, Ref } from "react";

interface MenuItemStylesParameters {
	level: number;
	active: boolean;
	disabled: boolean;
	open?: boolean;
	collapsed?: boolean;
}

type ElementStyles =
	| (CSSProperties & Record<string, any>)
	| ((parameters: MenuItemStylesParameters) => (CSSProperties & Record<string, any>) | undefined);

export interface MenuItemStyles {
	root?: ElementStyles;
	button?: ElementStyles;
	label?: ElementStyles;
	prefix?: ElementStyles;
	suffix?: ElementStyles;
	icon?: ElementStyles;
	subMenuContent?: ElementStyles;
	subMenuExpandIcon?: ElementStyles;
}

export interface MenuProperties {
	children?: ReactNode;
	ref?: Ref<HTMLDivElement>;
	closeOnClick?: boolean;
	menuItemStyles?: MenuItemStyles;
	renderExpandIcon?: (parameters: {
		level: number;
		collapsed: boolean;
		disabled: boolean;
		active: boolean;
		open: boolean;
	}) => ReactNode;
	transitionDuration?: number;
	rootStyles?: CSSProperties;
	className?: string;
}

export interface MenuItemProperties {
	children?: ReactNode;
	ref?: Ref<HTMLDivElement>;
	icon?: ReactNode;
	active?: boolean;
	disabled?: boolean;
	prefix?: ReactNode;
	suffix?: ReactNode;
	component?: string | React.ReactElement;
	rootStyles?: CSSProperties;
	onClick?: () => void;
	className?: string;
	isMobile?: boolean;
}

