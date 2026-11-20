import type { Dispatch, SetStateAction } from "react";
import { useCallback, useState } from "react";

import type { UseMobileNavigationDrawerResult } from "@/core/layout/hooks/Types";

/** One sub-panel's open state; the panel itself tells the parent drawer when it is on screen. */
export function useMobileNavigationDrawer(
	setParentDrawerOpen: Dispatch<SetStateAction<boolean>>,
): UseMobileNavigationDrawerResult {
	const [isDrawerOpen, setIsDrawerOpen] = useState(false);

	const openDrawer = useCallback(() => setIsDrawerOpen(true), []);

	const closePanel = useCallback(() => setIsDrawerOpen(false), []);

	const closeDrawer = useCallback(() => {
		setIsDrawerOpen(false);
		setParentDrawerOpen(false);
	}, [setParentDrawerOpen]);

	return { isDrawerOpen, openDrawer, closePanel, closeDrawer };
}
