import { Badge, Button, Group, Stack, Text } from "@mantine/core";
import { IconPlayerEject } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { RuntimeResident, RuntimeResidentState } from "@/features/runtime-resources/models/RuntimeResourcesModels";

const stateColors: Record<RuntimeResidentState, string> = {
	starting: "yellow",
	idle: "green",
	active: "blue",
	exited: "gray",
};

interface RuntimeResidentRowProps {
	readonly resident: RuntimeResident;
	readonly ejecting: boolean;
	readonly onEject: () => void;
}

// One image or transcription process in the widget. The eject unloads the whole runtime, never one model, which is
// why the accessible name names the runtime rather than the model.
export function RuntimeResidentRow({ resident, ejecting, onEject }: RuntimeResidentRowProps) {
	const { t } = useTranslation();
	const isImage = resident.runtime === "image";
	const key = resident.modelId ?? "starting";

	return (
		<Group justify="space-between" wrap="nowrap" gap="xs" data-testid={`runtime-resources-row-${resident.runtime}-${key}`}>
			<Stack gap={2} style={{ minWidth: 0 }}>
				<Text size="sm" truncate="end" c={resident.modelId === null ? "dimmed" : undefined}>
					{resident.modelId ?? t("runtimeResources.startingModel", "Starting…")}
				</Text>
				<Group gap={4}>
					<Badge size="xs" variant="outline" color="gray">
						{isImage
							? t("runtimeResources.runtime.image", "Images")
							: t("runtimeResources.runtime.transcription", "Transcription")}
					</Badge>
					{resident.backend === null ? null : (
						<Badge size="xs" variant="outline">
							{t(`pages.transcription.runtime.models.backend.${resident.backend}`)}
						</Badge>
					)}
					<Badge size="xs" variant="light" color={stateColors[resident.state]}>
						{t(`runtimeResources.state.${resident.state}`)}
					</Badge>
				</Group>
			</Stack>
			<Button
				size="compact-xs"
				variant="light"
				color="red"
				leftSection={<IconPlayerEject size={12} />}
				loading={ejecting}
				// A starting or exited process has nothing to unload yet (or any more); `canEject` mirrors the server's 409.
				disabled={!resident.canEject || ejecting || resident.state === "starting" || resident.state === "exited"}
				onClick={onEject}
				aria-label={
					isImage
						? t("runtimeResources.ejectImageRuntime", "Eject image runtime")
						: t("runtimeResources.ejectTranscriptionRuntime", "Eject transcription runtime")
				}
				data-testid={`runtime-resources-eject-${resident.runtime}`}
			>
				{t("pages.loadedModels.llamaCpp.eject", "Eject")}
			</Button>
		</Group>
	);
}
