import type { IconProps } from "@tabler/icons-react";
import {
	IconCloudCog,
	IconCpu,
	IconDashboard,
	IconHome,
	IconListDetails,
	IconMessageCircle,
	IconPlugConnected,
	IconServerCog,
	IconSettings,
} from "@tabler/icons-react";
import type { ForwardRefExoticComponent, RefAttributes } from "react";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";

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
	{ id: "home", icon: IconHome, translationKey: "navigation.home", to: nodeRoutePaths.home },
	{
		id: "dashboard",
		icon: IconDashboard,
		translationKey: "navigation.dashboard",
		to: nodeRoutePaths.dashboard,
	},
	{
		id: "chat",
		icon: IconMessageCircle,
		translationKey: "navigation.chat",
		to: nodeRoutePaths.chat,
	},
	{
		id: "binding",
		icon: IconPlugConnected,
		translationKey: "navigation.binding",
		to: nodeRoutePaths.binding,
	},
	{
		id: "node-settings",
		icon: IconSettings,
		translationKey: "navigation.nodeSettings",
		to: nodeRoutePaths.nodeSettings,
	},
	{
		id: "cloud-settings",
		icon: IconCloudCog,
		translationKey: "navigation.cloudSettings",
		to: nodeRoutePaths.cloudSettings,
	},
	{
		id: "models",
		icon: IconCpu,
		translationKey: "navigation.models",
		to: nodeRoutePaths.models,
	},
	{
		id: "manager",
		icon: IconServerCog,
		translationKey: "navigation.manager",
		to: nodeRoutePaths.manager,
	},
	{
		id: "invocations",
		icon: IconListDetails,
		translationKey: "navigation.invocations",
		to: nodeRoutePaths.invocations,
	},
];
