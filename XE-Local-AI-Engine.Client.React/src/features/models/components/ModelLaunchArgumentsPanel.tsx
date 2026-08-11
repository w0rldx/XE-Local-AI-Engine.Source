import { Alert, Button, Code, Group, Loader, Stack, Text, Textarea } from "@mantine/core";
import { IconAlertTriangle, IconArrowBackUp } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	deleteModelLaunchArgumentsMutation,
	getModelLaunchArgumentsOptions,
	getModelLaunchArgumentsQueryKey,
	putModelLaunchArgumentsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toast } from "@/core/ui/notifications/Toast";

interface ModelLaunchArgumentsPanelProps {
	modelName: string;
}

// Developer/advanced per-model llama.cpp launch-argument override. Self-contained (like ModelFitPanel): reads the
// current override and owns its save/clear mutations. The reserved process-contract flags (model path / host / port)
// are rejected by the backend with a clear message that surfaces via the mutation's error toast. The override is
// stored per model and applied the next time that model is (re)loaded.
export function ModelLaunchArgumentsPanel({ modelName }: ModelLaunchArgumentsPanelProps) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const query = useQuery(withResponseValidation(getModelLaunchArgumentsOptions({ path: { modelName } })));
	const serverValue = query.data?.rawArguments ?? "";
	const [value, setValue] = useState(serverValue);

	// Re-seed the editor when the fetched override arrives or the model changes.
	useEffect(() => {
		setValue(serverValue);
	}, [serverValue]);

	const invalidate = () =>
		queryClient.invalidateQueries({ queryKey: getModelLaunchArgumentsQueryKey({ path: { modelName } }) });

	const saveMutation = useMutation({
		...withResponseValidation(putModelLaunchArgumentsMutation()),
		onSuccess: async (response) => {
			setValue(response.rawArguments);
			toast.success(t("pages.models.launchArgs.saved", "Launch arguments saved. They apply the next time this model loads."));
			await invalidate();
		},
		onError: (error) => toast.error(apiErrorMessage(error, "Failed to save launch arguments")),
	});

	const clearMutation = useMutation({
		...withResponseValidation(deleteModelLaunchArgumentsMutation()),
		onSuccess: async () => {
			setValue("");
			toast.success(t("pages.models.launchArgs.reset", "Launch arguments reset to defaults."));
			await invalidate();
		},
		onError: (error) => toast.error(apiErrorMessage(error, "Failed to reset launch arguments")),
	});

	const isPending = saveMutation.isPending || clearMutation.isPending;
	const isDirty = value.trim() !== serverValue.trim();

	return (
		<Stack gap="md">
			<Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
				{t(
					"pages.models.launchArgs.warning",
					"Advanced: these raw flags are passed to llama.cpp when this model loads. An invalid flag can stop the model from starting — it only affects this model, and clearing the override restores the defaults. Changes take effect the next time the model loads.",
				)}
			</Alert>

			{query.isLoading ? <Loader size="sm" /> : null}

			{query.error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{apiErrorMessage(query.error, "Failed to load launch arguments")}
				</Alert>
			) : null}

			<Textarea
				label={t("pages.models.launchArgs.label", "Extra llama.cpp arguments")}
				description={
					<Text span={true} size="xs" c="dimmed">
						{t("pages.models.launchArgs.description", "Space-separated flags, for example ")}
						<Code>--top-k 40 --repeat-penalty 1.1</Code>
						{t("pages.models.launchArgs.descriptionSuffix", ". The model path, host and port are managed by the app and cannot be set here.")}
					</Text>
				}
				placeholder="--top-k 40 --repeat-penalty 1.1"
				autosize={true}
				minRows={2}
				maxRows={6}
				value={value}
				disabled={isPending}
				onChange={(event) => setValue(event.currentTarget.value)}
				data-testid="model-launch-args-input"
			/>

			<Group gap="sm">
				<Button
					onClick={() => saveMutation.mutate({ path: { modelName }, body: { rawArguments: value } })}
					loading={saveMutation.isPending}
					disabled={!isDirty || isPending}
					data-testid="model-launch-args-save"
				>
					{t("pages.models.launchArgs.save", "Save")}
				</Button>
				<Button
					variant="subtle"
					color="gray"
					leftSection={<IconArrowBackUp size={14} />}
					onClick={() => clearMutation.mutate({ path: { modelName } })}
					loading={clearMutation.isPending}
					disabled={!serverValue || isPending}
					data-testid="model-launch-args-reset"
				>
					{t("pages.models.launchArgs.reset", "Reset to default")}
				</Button>
			</Group>
		</Stack>
	);
}
