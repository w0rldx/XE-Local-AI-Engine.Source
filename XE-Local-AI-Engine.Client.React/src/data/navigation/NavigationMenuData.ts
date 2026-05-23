import type { IconProps } from "@tabler/icons-react";
import { IconHome, IconMessageCircle } from "@tabler/icons-react";
import type { ForwardRefExoticComponent, RefAttributes } from "react";

interface INavigationNestedLink {
	translationKey: string;
	to: string;
	onClick?: () => void;
}

export interface INavigationLink {
	id: string;
	icon: ForwardRefExoticComponent<IconProps & RefAttributes<SVGSVGElement>>;
	translationKey: string;
	to?: string;
	collapseIdentifier?: string;
	links?: INavigationNestedLink[];
	onClick?: () => void;
}

export const navigationLinks: INavigationLink[] = [
	{ id: "home", icon: IconHome, translationKey: "navigation.home", to: "/" },
	{
		id: "chat",
		icon: IconMessageCircle,
		translationKey: "navigation.chat",
		to: "/chat",
	},
];
