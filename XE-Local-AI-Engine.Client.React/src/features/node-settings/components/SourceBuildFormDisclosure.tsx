import { Button, Collapse, Stack } from "@mantine/core";
import { IconChevronDown, IconChevronRight } from "@tabler/icons-react";
import { type ReactNode, useId } from "react";
import { useTranslation } from "react-i18next";

// The build form of all three source-build cards, behind one disclosure.
//
// It exists for a cost, not for tidiness: every `GET .../source-build/prerequisites` really RUNS the toolchain — the
// node spawns `cmake --version`, `gcc`, `g++`, `ninja`/`make`, `git` (plus `readelf` for whisper, and
// `nvcc`/`nvidia-smi` or `glslc`/`vulkaninfo` for an accelerated backend) one after another, because "on PATH" and
// "runs" differ often enough to matter. Three cards rendering unconditionally meant ~20 child processes on every
// operator's first visit to Node Settings. The status queries and the hubs cost nothing comparable (each status
// endpoint returns in-memory state under a lock), so everything driven by them stays outside: what the card knows is
// always visible, and only what has to be MEASURED waits to be asked for.

export interface SourceBuildFormDisclosureProps {
	readonly opened: boolean;
	readonly onToggle: () => void;
	/** Test id of the collapsible region; the toggle gets `${testId}-toggle`. */
	readonly testId: string;
	readonly children: ReactNode;
}

export function SourceBuildFormDisclosure({ opened, onToggle, testId, children }: SourceBuildFormDisclosureProps) {
	const { t } = useTranslation();
	const contentId = useId();

	return (
		<>
			<Button
				variant="subtle"
				size="compact-sm"
				justify="start"
				leftSection={opened ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
				onClick={onToggle}
				aria-expanded={opened}
				aria-controls={contentId}
				data-testid={`${testId}-toggle`}
			>
				{t("components.sourceBuild.buildFromSource")}
			</Button>
			{/* The id sits on the Collapse itself, which stays mounted in both states: `keepMounted={false}` drops the
			    CONTENT so a closed form holds no focusable inputs, and an id one level down would leave `aria-controls`
			    dangling in exactly the state it has to describe. Same reasoning as the navigation rail's nested links. */}
			<Collapse id={contentId} expanded={opened} keepMounted={false} data-testid={testId}>
				<Stack gap="md">{children}</Stack>
			</Collapse>
		</>
	);
}
