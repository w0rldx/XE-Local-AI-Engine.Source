import { Button, Stack, Text } from "@mantine/core";
import type { ReactNode } from "react";
import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { CONFIRM_DIALOG_ATTRIBUTE, type ConfirmOptions } from "@/core/ui/models/Confirm";

export function ConfirmProvider({ children }: { children: ReactNode }) {
	const { t } = useTranslation();
	const [isOpen, setIsOpen] = useState(false);
	const [options, setOptions] = useState<ConfirmOptions>({});
	const resolveRejectRef = useRef<{
		resolve: (value: boolean) => void;
		reject: () => void;
	} | null>(null);
	// The open dialog's onClosed; a newer confirm replaces it, so a superseded dialog's callback never runs.
	const onClosedRef = useRef<(() => void) | undefined>(undefined);

	const confirm = useCallback(
		(newOptions: ConfirmOptions = {}) =>
			new Promise<boolean>((resolve, reject) => {
				setOptions(newOptions);
				onClosedRef.current = newOptions.onClosed;
				resolveRejectRef.current = { resolve, reject };
				setIsOpen(true);
			}),
		[],
	);

	const handleClose = useCallback(() => {
		setIsOpen(false);
		resolveRejectRef.current = null;
	}, []);

	const handleConfirm = useCallback(() => {
		resolveRejectRef.current?.resolve(true);
		handleClose();
	}, [handleClose]);

	const handleCancel = useCallback(() => {
		resolveRejectRef.current?.resolve(false);
		handleClose();
	}, [handleClose]);

	// Mantine reports the exit transition's end just before the update that unmounts the dialog; a frame later that
	// commit is done and focus inside it has fallen to the body. Taking the callback runs it once.
	const handleExitTransitionEnd = useCallback(() => {
		const onClosed = onClosedRef.current;
		onClosedRef.current = undefined;
		if (onClosed !== undefined) {
			requestAnimationFrame(onClosed);
		}
	}, []);

	// Read at call time, not captured at render: a flow resuming after an await must see a confirmation opened meanwhile.
	const hasPending = useCallback(() => resolveRejectRef.current !== null, []);

	const contextValue = useMemo(() => ({ confirm, hasPending }), [confirm, hasPending]);

	return (
		<ConfirmContext.Provider value={contextValue}>
			{children}
			<DialogShell
				opened={isOpen}
				onClose={handleCancel}
				onExitTransitionEnd={handleExitTransitionEnd}
				title={options.title || t("common.confirmation", "Confirmation")}
				size="sm"
				showCloseButton={false}
				enableFullScreenToggle={false}
				raised={true}
				attributes={{ content: { [CONFIRM_DIALOG_ATTRIBUTE]: "" } }}
			>
				<Stack gap="md">
					{options.description && (
						<Text
							className="leading-relaxed"
							style={{
								fontSize: "0.95rem",
								lineHeight: 2,
							}}
						>
							{options.description}
						</Text>
					)}
				</Stack>
				<div className="flex flex-col px-4 py-2 gap-0">
					<div className="flex mb-2">
						<div className="flex-grow" />
						<div className="flex flex-row ml-auto gap-4">
							<Button color="red" variant="subtle" onClick={handleCancel} data-testid="confirm-cancel">
								{options.cancellationText || t("common.cancel", "Cancel")}
							</Button>
							<Button color="primary" variant="filled" onClick={handleConfirm} data-testid="confirm-accept">
								{options.confirmationText || t("common.confirm", "Confirm")}
							</Button>
						</div>
					</div>
				</div>
			</DialogShell>
		</ConfirmContext.Provider>
	);
}
