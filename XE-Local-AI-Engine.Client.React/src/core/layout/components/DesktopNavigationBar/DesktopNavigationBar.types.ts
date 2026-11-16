import type { INavigationLink } from "@/core/layout/models/NavigationMenuData";

export interface IDesktopNavigationBarProperties {
	sideBarCollapsed: boolean;
	setSideBarCollapsed: (collapsed: boolean) => void;
}

interface IViewableNavigationNestedLink {
	label: string;
	to: string;
}

export interface IViewableNavigationLink {
	id: string;
	icon: INavigationLink["icon"];
	label: string;
	to?: string;
	onClick?: () => void;
	nestedLinks?: IViewableNavigationNestedLink[];
}
