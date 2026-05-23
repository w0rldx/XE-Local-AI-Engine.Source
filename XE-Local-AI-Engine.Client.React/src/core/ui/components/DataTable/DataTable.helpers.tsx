import type { ReactNode } from "react";

import { IconBaselineDensityLarge, IconBaselineDensityMedium, IconBaselineDensitySmall } from "@tabler/icons-react";

import type { DataTableDensityState } from "@/core/ui/components/DataTable/Types";

export const PAGE_SIZE_OPTIONS = ["10", "20", "50", "100"];

export function getDensityPadding(density: DataTableDensityState): string {
	switch (density) {
		case "xs":
			return "0.35rem";
		case "lg":
			return "0.9rem";
		default:
			return "0.6rem";
	}
}

export function getDensityTableSpacing(density: DataTableDensityState): "xs" | "sm" | "md" {
	if (density === "xs") {
		return "xs";
	}
	if (density === "md") {
		return "sm";
	}
	return "md";
}

export function getNextDensity(density: DataTableDensityState): DataTableDensityState {
	if (density === "xs") {
		return "md";
	}
	if (density === "md") {
		return "lg";
	}
	return "xs";
}

export function getDensityIcon(density: DataTableDensityState): ReactNode {
	if (density === "xs") {
		return <IconBaselineDensitySmall size={16} />;
	}
	if (density === "lg") {
		return <IconBaselineDensityLarge size={16} />;
	}
	return <IconBaselineDensityMedium size={16} />;
}
