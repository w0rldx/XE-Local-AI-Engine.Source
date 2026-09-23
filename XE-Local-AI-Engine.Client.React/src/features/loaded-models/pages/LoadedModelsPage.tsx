import { IconServer } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { RunningModelsPanel } from "@/features/loaded-models/components/RunningModelsPanel";
import type { RunningModel } from "@/features/loaded-models/models/RunningModelsModels";
import { useEjectRunningModel, useRunningModels } from "@/features/loaded-models/queries/useRunningModels";

export function LoadedModelsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const runningModelsQuery = useRunningModels();
	const ejectRunningMutation = useEjectRunningModel();

	// Ejects a llama.cpp running model. A graceful eject (force=false) waits for any in-flight generation to drain; the
	// outcome drives a distinct toast, and a timed-out-still-busy result offers a force eject that interrupts the run.
	const handleEjectRunning = async (model: RunningModel, force = false): Promise<void> => {
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
			});

			if (!confirmed) {
				return;
			}
		}

		ejectRunningMutation.mutate(
			{ modelName: model.modelName, role: model.role || undefined, force },
			{
				onSuccess: async (result) => {
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
							const forceConfirmed = await confirm({
								title: t("pages.loadedModels.llamaCpp.forceConfirm.title", "Force eject?"),
								description: t(
									"pages.loadedModels.llamaCpp.forceConfirm.description",
									"'{{modelName}}' is still generating. Force-eject anyway and interrupt the running turn?",
									{ modelName: model.modelName },
								),
								confirmationText: t("pages.loadedModels.llamaCpp.forceConfirm.action", "Force eject"),
								cancellationText: t("common.cancel", "Cancel"),
							});
							if (forceConfirmed) {
								await handleEjectRunning(model, true);
							}
							return;
						}
						default:
							return;
					}
				},
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("pages.loadedModels.llamaCpp.ejectError", "Could not eject the model."))),
			},
		);
	};

	return (
		<PageShell>
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
