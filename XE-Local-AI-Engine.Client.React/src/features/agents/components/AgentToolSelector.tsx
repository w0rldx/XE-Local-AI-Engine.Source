import { Alert, Badge, Checkbox, Group, Loader, Paper, Stack, Switch, Text } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle, IconWorldOff } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ToolCategoryBadge } from "@/features/tools/components/ToolCategoryBadge";
import { ToolSourceBadge } from "@/features/tools/components/ToolSourceBadge";
import { type ToolCatalogEntry, toToolDisplayName } from "@/features/tools/models/ToolCatalogModels";
import { useToolCatalog } from "@/features/tools/queries/useToolCatalog";

interface AgentToolSelectorProps {
	// Selected tool names and the per-tool approval overrides. Both are owned by the parent form.
	selectedToolNames: readonly string[];
	toolApprovals: Readonly<Record<string, boolean>>;
	// When false the whole selector is disabled and a warning is shown (model not tool-capable).
	toolCapable: boolean;
	onToggleTool: (toolName: string, selected: boolean) => void;
	onToggleApproval: (toolName: string, requiresApproval: boolean) => void;
	// The seeded Default Assistant: the backend ignores its allowed-tool list and offers it the whole capability-gated
	// offer, so the selector shows that offer checked and locked instead of the (empty) stored list.
	isDefaultAssistant?: boolean;
	// The Default Assistant's offer as the server computes it for the form's model; undefined while it loads.
	defaultOfferToolNames?: readonly string[];
	defaultOfferError?: boolean;
	// The node's web-access switch, for the hint on the web tools. Undefined while node settings load; null is a
	// never-saved switch, which the server reads as its default (off).
	webAccessEnabled?: boolean | null;
}

// Offered only when the node's web-access switch is on (WebAccessToolCatalog), whatever an agent lists.
const webAccessToolNames: ReadonlySet<string> = new Set(["web_search", "web_fetch"]);

// Unioned into every agent's offer by the backend (AskUserToolOffer.EnsureOffered), so it cannot be unchecked.
const askUserToolName = "ask_user";

// Synthesize a catalog entry for a tool that is selected on the definition but no longer present in the live
// catalog (e.g. an MCP tool whose server was disabled/removed). It is shown so the user can still see and
// deselect it; it defaults to requiresApproval=true (the strict default), an unknown source, and the fail-closed
// "Unknown" risk class (treated as approval-requiring) since its real class is no longer known. sessionScopeEligible
// is false for the same reason — a vanished tool cannot be shown as one the node will remember a decision for. This
// selector does not render that flag today; it is set so the synthesized entry stays a complete catalog entry.
function unknownToolEntry(name: string): ToolCatalogEntry {
	return {
		name,
		description: "",
		requiresApproval: true,
		source: { kind: "builtin", serverSlug: null },
		category: "Unknown",
		effectiveRequiresApproval: true,
		sessionScopeEligible: false,
	};
}

// Tool multi-select with a per-tool approval toggle. The tool catalog is fetched live (useToolCatalog) — the
// SAME source the chat surface renders — so it includes built-ins and tools from enabled MCP servers. When the
// selected model is not tool-capable the selector is disabled and a warning is surfaced (no silent no-op).
export function AgentToolSelector({
	selectedToolNames,
	toolApprovals,
	toolCapable,
	onToggleTool,
	onToggleApproval,
	isDefaultAssistant = false,
	defaultOfferToolNames,
	defaultOfferError = false,
	webAccessEnabled,
}: AgentToolSelectorProps) {
	const { t } = useTranslation();
	const catalogQuery = useToolCatalog();

	// The Default Assistant's checked set is the server's offer, never the (empty) stored list.
	const checkedToolNames = isDefaultAssistant ? (defaultOfferToolNames ?? []) : selectedToolNames;
	const defaultOfferLoading = isDefaultAssistant && defaultOfferToolNames === undefined && !defaultOfferError;

	// Render the live catalog plus any checked tools that are not in it (so they stay visible and deselectable).
	// Checked-but-absent tools are appended after the catalog, in order.
	const rows = useMemo<ToolCatalogEntry[]>(() => {
		const catalog = catalogQuery.data ?? [];
		const catalogNames = new Set(catalog.map((tool) => tool.name));
		const orphanSelected: ToolCatalogEntry[] = [];
		for (const name of checkedToolNames) {
			if (!catalogNames.has(name)) {
				orphanSelected.push(unknownToolEntry(name));
			}
		}
		return [...catalog, ...orphanSelected];
	}, [catalogQuery.data, checkedToolNames]);
	const checkedToolNameSet = useMemo(() => new Set(checkedToolNames), [checkedToolNames]);
	const webAccessOff = webAccessEnabled === false || webAccessEnabled === null;

	return (
		<Stack gap="xs" data-testid="agent-tool-selector">
			<Text size="sm" fw={600}>
				{t("pages.agents.form.tools.label", "Tools")}
			</Text>
			{!toolCapable ? (
				<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="agent-tool-capability-warning">
					{t(
						"pages.agents.form.tools.notCapableWarning",
						"The selected model is not tool-capable. Tool selection is disabled. Pick a tool-capable model to enable tools.",
					)}
				</Alert>
			) : null}
			{isDefaultAssistant ? (
				<Alert color="blue" icon={<IconInfoCircle size={16} />} data-testid="agent-tool-default-offer-note">
					{t("pages.agents.form.tools.defaultAssistantNote", "The default assistant uses every tool available to its model.")}
				</Alert>
			) : null}

			{catalogQuery.isLoading || defaultOfferLoading ? (
				<Group gap="sm" data-testid="agent-tool-catalog-loading">
					<Loader size="sm" />
					<Text c="dimmed" size="sm">
						{t("pages.agents.form.tools.loading", "Loading tools…")}
					</Text>
				</Group>
			) : null}

			{defaultOfferError ? (
				<InlineErrorAlert
					message={t("pages.agents.form.tools.defaultOfferError", "Could not load the default assistant's tools.")}
					data-testid="agent-tool-default-offer-error"
				/>
			) : null}

			{catalogQuery.error ? (
				<InlineErrorAlert
					message={t("pages.agents.form.tools.loadError", "Could not load the tool catalog.")}
					data-testid="agent-tool-catalog-error"
				/>
			) : null}

			{!catalogQuery.isLoading && !catalogQuery.error && rows.length === 0 ? (
				<Text size="xs" c="dimmed" data-testid="agent-tool-catalog-empty">
					{t("pages.agents.form.tools.empty", "No tools available.")}
				</Text>
			) : null}

			{(isDefaultAssistant && (defaultOfferLoading || defaultOfferError) ? [] : rows).map((tool) => {
				// The server's Default Assistant offer already carries ask_user whenever the model gets it.
				const isAlwaysOffered = !isDefaultAssistant && toolCapable && tool.name === askUserToolName;
				const isSelected = isAlwaysOffered || checkedToolNameSet.has(tool.name);
				const locked = !toolCapable || isDefaultAssistant || isAlwaysOffered;
				// A custom (user-defined) tool always requires approval at runtime (forced by the tool registry),
				// so the per-tool approval switch is pinned on and locked — flipping it would be a misleading no-op.
				const isCustom = tool.source.kind === "custom";
				// The Default Assistant takes no per-agent overrides, only the node policy the catalog already applied.
				const requiresApproval = isDefaultAssistant
					? tool.effectiveRequiresApproval
					: isCustom
						? true
						: (toolApprovals[tool.name] ?? tool.requiresApproval);

				return (
					<Paper withBorder={true} p="xs" key={tool.name} data-testid={`agent-tool-row-${tool.name}`}>
						<Stack gap={4}>
							<Group justify="space-between" align="center" wrap="nowrap">
								<Checkbox
									checked={isSelected}
									disabled={locked}
									label={
										<Group gap="xs" wrap="nowrap" align="center">
											<Text size="sm" fw={600} ff="monospace">
												{toToolDisplayName(tool.name)}
											</Text>
											<ToolSourceBadge source={tool.source} />
											<ToolCategoryBadge category={tool.category} effectiveRequiresApproval={tool.effectiveRequiresApproval} />
										</Group>
									}
									onChange={(event) => onToggleTool(tool.name, event.currentTarget.checked)}
									data-testid={`agent-tool-checkbox-${tool.name}`}
								/>
								<Switch
									size="sm"
									checked={requiresApproval}
									disabled={locked || !isSelected || isCustom}
									label={
										<Badge size="xs" variant="light" color={requiresApproval ? "orange" : "teal"}>
											{requiresApproval
												? webAccessToolNames.has(tool.name)
													? t("pages.agents.form.tools.webReviewed", "Asks before sending, reviews the result")
													: t("pages.agents.form.tools.requiresApproval", "requires approval")
												: t("pages.agents.form.tools.autoExecute", "auto-execute")}
										</Badge>
									}
									onChange={(event) => onToggleApproval(tool.name, event.currentTarget.checked)}
									data-testid={`agent-tool-approval-${tool.name}`}
								/>
							</Group>
							{isAlwaysOffered ? (
								<Text size="xs" c="dimmed" data-testid="agent-tool-always-offered-note">
									{t("pages.agents.form.tools.alwaysOffered", "Always available: an agent can always ask you a question.")}
								</Text>
							) : null}
							{webAccessOff && webAccessToolNames.has(tool.name) ? (
								<ButtonLink
									to="/node-settings"
									search={{ section: "knowledge" }}
									variant="subtle"
									color="yellow"
									size="compact-xs"
									leftSection={<IconWorldOff size={14} />}
									style={{ alignSelf: "flex-start" }}
									data-testid={`agent-tool-web-access-hint-${tool.name}`}
								>
									{t("pages.agents.form.tools.webAccessRequired", "Requires web access in Node Settings")}
								</ButtonLink>
							) : null}
							{tool.description ? (
								<Text size="xs" c="dimmed">
									{tool.description}
								</Text>
							) : null}
						</Stack>
					</Paper>
				);
			})}
		</Stack>
	);
}
