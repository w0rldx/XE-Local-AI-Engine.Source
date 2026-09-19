import { Button, Group, Stack, Text, Tooltip } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";
import type { BenchmarkProjectDetail, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { isRunActive, useDeleteBenchmarkProject } from "@/features/benchmarks/queries/useBenchmarks";

interface BenchmarkProjectDeleteButtonProps {
	project: BenchmarkProjectDetail;
	/**
	 * Already loaded by the workspace for the item editor; named in the confirmation so the loss is concrete.
	 *
	 * `undefined` means the count is not known for THIS project — the item query is still in flight after a project
	 * switch, or it failed. A destructive confirmation must never under-report, and "0 task items" for a project that
	 * is about to lose several is exactly that, so the count-free sentence is used instead of a zero.
	 */
	taskItemCount: number | undefined;
	/**
	 * The runs of this project that the page has loaded. Only their ACTIVE-ness is read here: the count named in the
	 * confirmation comes from the project's own server-side `runCount`, which is authoritative and always present,
	 * while this list is the first page of at most 200 and could in principle miss an active run on a later page.
	 * That is a convenience gate, not the guarantee — the node refuses an active run itself, with 409 `ActiveRun`.
	 */
	runs: readonly Pick<BenchmarkRunSummary, "primaryStatus" | "judge" | "fidelity">[];
}

/**
 * Deletes the selected project together with its finished runs and everything hanging off them. The node refuses only
 * while a run is still in play — queued, generating, judging or being compared — so that is the one state the control
 * is disabled in, and the tooltip names the way out.
 *
 * The dialog stays open on a failure (a version conflict is the realistic one: another tab edited the project between
 * this page's last read and the click) so the operator can retry after refreshing rather than re-finding the project.
 * Nothing navigates on success: the page controller notices the project has left the list and selects another one.
 */
export function BenchmarkProjectDeleteButton({ project, taskItemCount, runs }: BenchmarkProjectDeleteButtonProps) {
	const { t } = useTranslation();
	const [confirmOpen, setConfirmOpen] = useState(false);
	const deleteProject = useDeleteBenchmarkProject();
	const blocked = runs.some(isRunActive);

	return (
		<>
			<Tooltip
				label={
					blocked
						? t(
								"pages.benchmarks.project.deleteBlocked",
								"A run of this project is still going. Wait for it to finish or cancel it, then delete the project.",
							)
						: t("pages.benchmarks.project.deleteHint", "Deletes the project with its runs, task items and judge history.")
				}
				multiline={true}
				w={280}
			>
				<span>
					<Button
						variant="default"
						color="red"
						leftSection={<IconTrash size={16} />}
						disabled={blocked}
						onClick={() => setConfirmOpen(true)}
						data-testid="benchmark-project-delete"
					>
						{t("common.delete", "Delete")}
					</Button>
				</span>
			</Tooltip>

			<DialogShell
				opened={confirmOpen}
				onClose={() => setConfirmOpen(false)}
				title={t("pages.benchmarks.project.deleteTitle", "Delete this benchmark project?")}
				size="md"
				data-testid="benchmark-project-delete-confirm"
			>
				<Stack gap="md">
					<Text>
						{taskItemCount === undefined
							? t(
									"pages.benchmarks.project.deleteConfirmUnknownCount",
									"“{{name}}” is deleted with its task items — their prompts, reference answers and verifier settings — and its whole judge history. This cannot be undone.",
									{ name: project.name },
								)
							: // Pluralised: the bundle holds `_one`/`_other` and i18next picks by `count`. The default written here is
								// the `_other` form, the convention CheckI18nDefaults expects.
								t(
									"pages.benchmarks.project.deleteConfirm",
									"“{{name}}” is deleted with its {{count}} task items — their prompts, reference answers and verifier settings — and its whole judge history. This cannot be undone.",
									{ name: project.name, count: taskItemCount },
								)}
					</Text>
					{/* The run count is the project's own server-side figure, so it is always known and never needs the
					    count-free form the task items have. Its own line rather than a clause: this is the half of the
					    deletion an operator is most likely to regret, and it must not read as an aside. */}
					{project.runCount > 0 ? (
						<Text fw={700} data-testid="benchmark-project-delete-runs">
							{t(
								"pages.benchmarks.project.deleteConfirmRuns",
								"Its {{count}} runs go with it, including every result, transcript and judge verdict.",
								{ count: project.runCount },
							)}
						</Text>
					) : null}
					<Group justify="flex-end">
						<Button variant="default" onClick={() => setConfirmOpen(false)}>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button
							color="red"
							loading={deleteProject.isPending}
							onClick={() =>
								deleteProject.mutate(
									{ projectId: project.id, expectedVersion: project.version },
									{
										onSuccess: () => {
											setConfirmOpen(false);
											toast.success(t("pages.benchmarks.project.deleted", "Benchmark project deleted."));
										},
										onError: (error) =>
											toast.error(
												apiErrorMessage(
													error,
													t("pages.benchmarks.project.errors.delete", "Could not delete this benchmark project."),
												),
											),
									},
								)
							}
							data-testid="benchmark-project-delete-accept"
						>
							{t("common.delete", "Delete")}
						</Button>
					</Group>
				</Stack>
			</DialogShell>
		</>
	);
}
