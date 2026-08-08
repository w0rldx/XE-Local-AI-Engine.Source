import type { Dispatch, SetStateAction } from "react";
import { useCallback, useEffect, useRef, useState } from "react";

import type { UseMobileNavigationDrawerResult } from "@/core/layout/hooks/Types";

export function useMobileNavigationDrawer(
	setParentDrawerOpen: Dispatch<SetStateAction<boolean>>,
): UseMobileNavigationDrawerResult {
	const [isDrawerOpen, setIsDrawerOpen] = useState(false);
	const drawerReference = useRef<HTMLDivElement>(null);
	const menuReference = useRef<HTMLDivElement>(null);

	useEffect(() => {
		const handleClickOutside = (event: MouseEvent) => {
			const target = event.target as Node;
			const isClickInsideDrawer = drawerReference.current?.contains(target);
			const isClickInsideMenu = menuReference.current?.contains(target);

			if (isDrawerOpen && !isClickInsideDrawer && !isClickInsideMenu) {
				setIsDrawerOpen(false);
			}
		};

		document.addEventListener("mousedown", handleClickOutside);
		return () => {
			document.removeEventListener("mousedown", handleClickOutside);
		};
	}, [isDrawerOpen]);

	const openDrawer = useCallback(() => {
		setIsDrawerOpen(true);
	}, []);

	const closeDrawer = useCallback(() => {
		setIsDrawerOpen(false);
		setParentDrawerOpen(false);
	}, [setParentDrawerOpen]);

	return {
		isDrawerOpen,
		setIsDrawerOpen,
		drawerReference,
		menuReference,
		openDrawer,
		closeDrawer,
	};
}
