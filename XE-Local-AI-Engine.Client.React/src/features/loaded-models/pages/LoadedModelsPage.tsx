import { IconServer } from "@tabler/icons-react";
import { useRef } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { MAIN_CONTENT_ID } from "@/core/layout/constants/LayoutElementIds";
import type { EjectRunningModelResult, RunningModel } from "@/core/loaded-models/models/RunningModelsModels";
import { useEjectRunningModel, useRunningModels } from "@/core/loaded-models/queries/useRunningModels";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { CONFIRM_DIALOG_ATTRIBUTE } from "@/core/ui/models/Confirm";
import { toast } from "@/core/ui/notifications/Toast";
import { RunningModelsPanel } from "@/features/loaded-models/components/RunningModelsPanel";

export function LoadedModelsPage() {
	const { t } = useTranslation();
	const { confirm, hasPending } = useConfirm();

	const runningModelsQuery = useRunningModels();
	const ejectRunningMutation = useEjectRunningModel();
	// React detaches this ref when the page unmounts, while ConfirmProvider above the routes can still resolve this flow.
	const pageRef = useRef<HTMLDivElement>(null);
	const latestEjectRef = useRef(0);

	// Ejects a llama.cpp running model. A graceful eject (force=false) waits for any in-flight generation to drain; the
	// outcome drives a distinct toast, and a timed-out-still-busy result offers a force eject that interrupts the run.
	const handleEjectRunning = async (model: RunningModel, restoreFocus: () => void, force = false): Promise<void> => {
		latestEjectRef.current += 1;
		const ejectNumber = latestEjectRef.current;
		// The flow outlives the page, and the one ConfirmProvider above the routes holds a single resolver: after an unmount
		// or a newer eject, no dialog opens and no answer to a still-open one sends a request.
		const stale = () => pageRef.current === null || ejectNumber !== latestEjectRef.current;
		// Once the page has gone, nothing on it can take focus back; the shell's main landmark does, so focus never rests
		// on the body.
		const onClosed = () => {
			restoreFocus();
			const active = document.activeElement;
			if (pageRef.current === null && (active === null || active === document.body)) {
				document.getElementById(MAIN_CONTENT_ID)?.focus();
			}
		};
		if (!force) {
			const confirmed = await confirm({
				title: t("pages.loadedModels.llamaCpp.ejectConfirm.title", "Eject model"),
				description: t(
					"pages.loadedModels.llamaCpp.ejectConfirm.description",
					"Eject '{{modelName}}' from the llama.cpp runtime? Memory frees after any in-flight generation completes; a running response is not interrupted.",
					{ modelName: model.modelName },
				),
				confirmationText: t("pages.loadedModels.llamaCpp.ejectConfirm.action", "Eject"),
				cancellationText: t("common.cancel", "Cancel"),
				onClosed,
			});

			if (!confirmed || stale()) {
				return;
			}
		}

		let result: EjectRunningModelResult;
		try {
			result = await ejectRunningMutation.mutateAsync({ modelName: model.modelName, role: model.role || undefined, force });
		} catch (error) {
			toast.error(apiErrorMessage(error, t("pages.loadedModels.llamaCpp.ejectError", "Could not eject the model.")));
			return;
		}

		switch (result.outcome) {
			case "ejected":
				toast.success(t("pages.loadedModels.llamaCpp.ejectOutcome.ejected", "Model ejected."));
				return;
			case "forced":
				toast.info(
					t("pages.loadedModels.llamaCpp.ejectOutcome.forced", "Model ejected — the in-flight generation was interrupted."),
				);
				return;
			case "not_running":
				toast.info(t("pages.loadedModels.llamaCpp.ejectOutcome.notRunning", "That model was not running."));
				return;
			case "timed_out_still_busy": {
				toast.warning(
					t(
						"pages.loadedModels.llamaCpp.ejectOutcome.timedOutStillBusy",
						"'{{modelName}}' is still finishing a response, so it was left running.",
						{ modelName: model.modelName },
					),
				);
				// A late prompt must not take over: not another flow's pending confirmation (opening one replaces its
				// resolver), not a dialog the user moved to meanwhile. The confirmation dialog itself may still hold focus
				// while it closes; that is this flow's own first answer. Re-Eject offers force again.
				const focusedDialog = document.activeElement?.closest('[role="dialog"]');
				if (stale() || hasPending?.() || (focusedDialog && !focusedDialog.hasAttribute(CONFIRM_DIALOG_ATTRIBUTE))) {
					return;
				}
				const forceConfirmed = await confirm({
					title: t("pages.loadedModels.llamaCpp.forceConfirm.title", "Force eject?"),
					description: t(
						"pages.loadedModels.llamaCpp.forceConfirm.description",
						"'{{modelName}}' is still generating. Force-eject anyway and interrupt the running turn?",
						{ modelName: model.modelName },
					),
					confirmationText: t("pages.loadedModels.llamaCpp.forceConfirm.action", "Force eject"),
					cancellationText: t("common.cancel", "Cancel"),
					onClosed,
				});
				if (forceConfirmed && !stale()) {
					await handleEjectRunning(model, restoreFocus, true);
				}
				return;
			}
			default:
				return;
		}
	};

	return (
		<PageShell ref={pageRef}>
			<PageHeader
				icon={<IconServer size={24} />}
				title={t("pages.loadedModels.title", "Loaded models")}
				subtitle={t(
					"pages.loadedModels.subtitle",
					"Models the local runtime is currently holding in memory (RAM/VRAM). Eject frees that memory — gracefully, after any in-flight generation completes.",
				)}
			/>

			<RunningModelsPanel
				runningModels={runningModelsQuery.data ?? []}
				isLoading={runningModelsQuery.isLoading}
				error={runningModelsQuery.error}
				onEject={handleEjectRunning}
				ejectingModelName={ejectRunningMutation.isPending ? (ejectRunningMutation.variables?.modelName ?? null) : null}
			/>
		</PageShell>
	);
}
