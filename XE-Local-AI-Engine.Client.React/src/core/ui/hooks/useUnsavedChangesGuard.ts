import { useBlocker } from "@tanstack/react-router";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";

export interface UseUnsavedChangesGuardOptions {
	/** When true, navigation away (and tab close) is blocked until the user confirms. */
	isDirty: boolean;
}

/**
 * Blocks in-app navigation and browser tab/refresh while a form has unsaved edits.
 *
 * Wraps TanStack Router's `useBlocker` with `withResolver: true`; when a blocked
 * transition occurs it drives the shared promise-based `useConfirm()` dialog and
 * either proceeds (discard) or resets (keep editing) based on the result. A no-op
 * while `isDirty` is false — the blocker is disabled so non-dirty forms never
 * intercept navigation.
 */
export function useUnsavedChangesGuard({ isDirty }: UseUnsavedChangesGuardOptions): void {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const { status, proceed, reset } = useBlocker({
		shouldBlockFn: () => isDirty,
		withResolver: true,
		enableBeforeUnload: isDirty,
		disabled: !isDirty,
	});

	// A blocked transition can re-render the hook while the confirm dialog is still
	// open; without this guard the effect would re-open the prompt on every render.
	const promptOpenRef = useRef(false);

	useEffect(() => {
		if (status !== "blocked" || promptOpenRef.current) {
			return;
		}

		promptOpenRef.current = true;

		confirm({
			title: t("components.dialogShell.unsavedTitle", "Unsaved changes"),
			description: t("components.dialogShell.unsavedDescription", "You have unsaved changes. Discard them and leave?"),
			confirmationText: t("common.discard", "Discard"),
			cancellationText: t("common.keepEditing", "Keep editing"),
		})
			.then((shouldDiscard) => {
				if (shouldDiscard) {
					proceed?.();
				} else {
					reset?.();
				}
			})
			.finally(() => {
				promptOpenRef.current = false;
			});
	}, [status, proceed, reset, confirm, t]);
}
