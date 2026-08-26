import { Alert } from "@mantine/core";
import { IconLock } from "@tabler/icons-react";
import type { ComponentProps } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { BenchmarkProjectForm } from "@/features/benchmarks/components/BenchmarkProjectForm";

interface BenchmarkProjectEditorDialogProps {
	mode: "create" | "edit" | null;
	isFrozen: boolean;
	onClose: () => void;
	formProps: ComponentProps<typeof BenchmarkProjectForm>;
}

/** Presents the project form and its frozen-project context while the page controller owns mutations. */
export function BenchmarkProjectEditorDialog({ mode, isFrozen, onClose, formProps }: BenchmarkProjectEditorDialogProps) {
	const { t } = useTranslation();
	return (
		<DialogShell
			opened={mode !== null}
			onClose={onClose}
			title={
				mode === "create"
					? t("pages.benchmarks.project.create", "New project")
					: t("pages.benchmarks.project.edit", "Benchmark project")
			}
			size="lg"
		>
			{mode === "edit" && isFrozen ? (
				<Alert mb="md" color="blue" icon={<IconLock size={16} />}>
					{t(
						"pages.benchmarks.project.frozenExplanation",
						"This project is frozen while runs exist. Delete its terminal runs to edit it again.",
					)}
				</Alert>
			) : null}
			<BenchmarkProjectForm {...formProps} />
		</DialogShell>
	);
}
