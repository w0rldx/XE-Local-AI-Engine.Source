// A web_fetch Tool node's link allow-list (ADR 0017, decision 6). The list is the consent an unattended run cannot ask
// for, so the first time an operator opens it they acknowledge that listed pages reach the next model unreviewed. The
// acknowledgement rides the per-user tutorial state under its own key; until it is given, the list cannot be edited.

import { ActionIcon, Alert, Button, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconPlus, IconTrash } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import {
	getTutorialStateOptions,
	getTutorialStateQueryKey,
	saveTutorialStateMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

const allowlistNoticeKey = "web-graph-allowlist-notice";

function useAllowlistNotice() {
	const queryClient = useQueryClient();
	const state = useQuery(withResponseValidation(getTutorialStateOptions()));
	const save = useMutation(withResponseValidation(saveTutorialStateMutation()));
	// Optimistic, so the list unlocks on the click; a failed save only means the notice shows once more next time.
	const [acknowledgedHere, setAcknowledgedHere] = useState(false);
	const stored = state.data?.entries?.some((entry) => entry.key === allowlistNoticeKey && entry.status === "completed") === true;

	return {
		isResolved: state.isSuccess || state.isError,
		acknowledged: acknowledgedHere || stored,
		acknowledge: () => {
			setAcknowledgedHere(true);
			save.mutate(
				{ body: { key: allowlistNoticeKey, status: "completed" } },
				{ onSuccess: () => queryClient.invalidateQueries({ queryKey: getTutorialStateQueryKey() }).catch(() => undefined) },
			);
		},
	};
}

export interface GraphWorkflowAllowedUrlsFieldProps {
	readonly allowedUrls: readonly string[];
	readonly onChange: (allowedUrls: readonly string[]) => void;
	readonly error: string | undefined;
	readonly onTouch: () => void;
	readonly readOnly: boolean;
}

export function GraphWorkflowAllowedUrlsField({
	allowedUrls,
	onChange,
	error,
	onTouch,
	readOnly,
}: GraphWorkflowAllowedUrlsFieldProps) {
	const { t } = useTranslation();
	const notice = useAllowlistNotice();
	const locked = readOnly || !notice.acknowledged;

	return (
		<Stack gap="xs" data-testid="gw-node-config-allowed-urls">
			<Group justify="space-between" wrap="wrap">
				<Text size="sm" fw={500}>
					{t("pages.graphWorkflows.config.allowedUrls", "Allowed links")}
				</Text>
				<Button
					size="xs"
					variant="light"
					leftSection={<IconPlus size={14} />}
					disabled={locked}
					onClick={() => onChange([...allowedUrls, ""])}
					data-testid="gw-node-config-allowed-url-add"
				>
					{t("pages.graphWorkflows.config.addAllowedUrl", "Add link")}
				</Button>
			</Group>
			<Text size="xs" c="dimmed">
				{t(
					"pages.graphWorkflows.config.allowedUrlsHelp",
					"web_fetch reads only pages under these links: same scheme, host and port, and a path at or below the link's path. Private and local addresses stay blocked even when listed.",
				)}
			</Text>
			{!readOnly && notice.isResolved && !notice.acknowledged ? (
				<Alert
					color="yellow"
					icon={<IconAlertTriangle size={16} />}
					title={t("pages.graphWorkflows.config.allowlistNoticeTitle", "Pages on these links reach the model unreviewed")}
					data-testid="gw-node-config-allowlist-notice"
				>
					<Stack gap="xs" align="flex-start">
						<Text size="sm">
							{t(
								"pages.graphWorkflows.config.allowlistNoticeBody",
								"A workflow run fetches these pages without asking anyone, and their text goes straight to the next model. A page can contain instructions written to mislead that model. List only sites you trust.",
							)}
						</Text>
						<Button size="xs" onClick={notice.acknowledge} data-testid="gw-node-config-allowlist-notice-ack">
							{t("pages.graphWorkflows.config.allowlistNoticeAcknowledge", "I understand")}
						</Button>
					</Stack>
				</Alert>
			) : null}
			{allowedUrls.length === 0 ? (
				<Text size="xs" c="dimmed" data-testid="gw-node-config-no-allowed-urls">
					{t("pages.graphWorkflows.config.noAllowedUrls", "None yet. The node cannot fetch anything until you add a link.")}
				</Text>
			) : (
				allowedUrls.map((url, index) => (
					// biome-ignore lint/suspicious/noArrayIndexKey: the URL is the field being edited; rows are appended and removed, never reordered.
					<Group key={`allowed-url-${index}`} gap="xs" align="flex-end" wrap="nowrap">
						<TextInput
							aria-label={t("pages.graphWorkflows.config.allowedUrls", "Allowed links")}
							placeholder="https://docs.example.com/guide"
							value={url}
							disabled={locked}
							style={{ flex: 1, minWidth: 0 }}
							onBlur={onTouch}
							onChange={(event) => {
								const next = event.currentTarget.value;
								onChange(allowedUrls.map((candidate, position) => (position === index ? next : candidate)));
							}}
							data-testid={`gw-node-config-allowed-url-${index}`}
						/>
						<ActionIcon
							variant="subtle"
							color="red"
							disabled={locked}
							aria-label={t("pages.graphWorkflows.config.removeAllowedUrl", "Remove link")}
							onClick={() => onChange(allowedUrls.filter((_, position) => position !== index))}
							data-testid={`gw-node-config-allowed-url-remove-${index}`}
						>
							<IconTrash size={16} />
						</ActionIcon>
					</Group>
				))
			)}
			{error ? (
				<Text size="xs" c="red" data-testid="gw-node-config-allowed-urls-error">
					{error}
				</Text>
			) : null}
		</Stack>
	);
}
