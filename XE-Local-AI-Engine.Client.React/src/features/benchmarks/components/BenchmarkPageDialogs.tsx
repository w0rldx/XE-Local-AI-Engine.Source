import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { BenchmarkConfirmationDialog } from "@/features/benchmarks/components/BenchmarkConfirmationDialog";
import { BenchmarkLaunchMatrix } from "@/features/benchmarks/components/BenchmarkLaunchMatrix";
import type { BenchmarksPageController } from "@/features/benchmarks/hooks/useBenchmarksPageController";

interface BenchmarkPageDialogsProps {
	readonly controller: BenchmarksPageController;
	/** The agent definitions a benchmark may run, for the matrix's agent-variant axis. */
	readonly agents: readonly { id: string; name: string }[];
}

export function BenchmarkPageDialogs({ controller, agents }: BenchmarkPageDialogsProps) {
	const {
		t,
		detail,
		updateJudge,
		matrixOpen,
		setMatrixOpen,
		modelsQuery,
		matrixRejections,
		startBatch,
		startMatrix,
		confirmMode,
		setConfirmMode,
		affectedRunCount,
		rejudgeProject,
		confirmPendingChange,
		leafItemCount,
		medianRunMs,
	} = controller;
	return (
		<>
			<DialogShell
				opened={matrixOpen && detail !== undefined}
				onClose={() => setMatrixOpen(false)}
				title={t("pages.benchmarks.matrix.title", "Batch benchmark runs")}
				size="lg"
				data-testid="benchmark-matrix-dialog"
			>
				<BenchmarkLaunchMatrix
					models={modelsQuery.data ?? []}
					agents={agents}
					projectAgentId={detail?.agentDefinitionId ?? ""}
					leafItemCount={leafItemCount}
					medianRunMs={medianRunMs}
					rejected={matrixRejections}
					isSubmitting={startBatch.isPending}
					onSubmit={startMatrix}
					onCancel={() => setMatrixOpen(false)}
				/>
			</DialogShell>

			<BenchmarkConfirmationDialog
				mode={confirmMode}
				projectId={detail?.id}
				affectedRunCount={affectedRunCount}
				isPending={updateJudge.isPending || rejudgeProject.isPending}
				onClose={() => setConfirmMode(null)}
				onConfirm={confirmPendingChange}
			/>
		</>
	);
}
