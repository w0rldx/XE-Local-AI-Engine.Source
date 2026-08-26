import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { useAgentDefinitions } from "@/features/agents/queries/useAgentDefinitions";
import { BenchmarkPageDialogs } from "@/features/benchmarks/components/BenchmarkPageDialogs";
import { BenchmarkProjectEditorDialog } from "@/features/benchmarks/components/BenchmarkProjectEditorDialog";
import { BenchmarkProjectWorkspace } from "@/features/benchmarks/components/BenchmarkProjectWorkspace";
import { BenchmarkRunResults } from "@/features/benchmarks/components/BenchmarkRunResults";
import { type BenchmarksPageProps, useBenchmarksPageController } from "@/features/benchmarks/hooks/useBenchmarksPageController";

export function BenchmarksPage(props: BenchmarksPageProps = {}) {
	const controller = useBenchmarksPageController(props);
	const agentsQuery = useAgentDefinitions();
	return (
		<PageShell>
			<BenchmarkProjectWorkspace controller={controller} />
			<BenchmarkRunResults controller={controller} />
			<BenchmarkProjectEditorDialog
				key={`${controller.editorMode}-${controller.detail?.id ?? "new"}`}
				mode={controller.editorMode}
				isFrozen={controller.editorMode === "edit" && controller.detail?.isFrozen === true}
				onClose={() => {
					controller.setEditorMode(null);
					controller.setSaveError(null);
				}}
				formProps={{
					initialValues: controller.editorDraft,
					projectId: controller.editorMode === "edit" ? controller.detail?.id : undefined,
					agents: (agentsQuery.data ?? []).filter((agent) => agent.kind === "Single"),
					models: controller.allModelsQuery.data ?? [],
					presets: controller.presetsQuery.data,
					frozen: controller.editorMode === "edit" && controller.detail?.isFrozen,
					isSaving: controller.createProject.isPending || controller.updateProject.isPending || controller.updateJudge.isPending,
					saveError: controller.saveError,
					onSubmit: controller.saveProject,
					onCancel: () => controller.setEditorMode(null),
				}}
			/>
			<BenchmarkPageDialogs controller={controller} />
		</PageShell>
	);
}
