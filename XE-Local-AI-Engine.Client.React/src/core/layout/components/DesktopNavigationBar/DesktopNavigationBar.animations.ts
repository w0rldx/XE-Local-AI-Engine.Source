import type { Variants } from "framer-motion";

import { SIDEBAR_WIDTH_COLLAPSED, SIDEBAR_WIDTH_EXPANDED } from "@/core/layout/constants/LayoutBreakpoints";

export const MOTION_SPEC = { duration: 0.2, ease: "easeOut" } as const;

export const navVariants: Variants = {
	expanded: { width: SIDEBAR_WIDTH_EXPANDED },
	collapsed: { width: SIDEBAR_WIDTH_COLLAPSED },
};

export const labelVariants: Variants = {
	expanded: {
		opacity: 1,
		width: "auto",
		transition: {
			duration: 0.2,
			ease: "easeOut",
			opacity: { delay: 0.12, duration: 0.08, ease: "easeIn" },
		},
	},
	collapsed: {
		opacity: 0,
		width: 0,
		transition: {
			duration: 0.2,
			ease: "easeOut",
			opacity: { duration: 0.06, ease: "easeOut" },
		},
	},
};

export const logoMarkVariants: Variants = {
	expanded: { x: 0 },
	collapsed: { x: 0 },
};
