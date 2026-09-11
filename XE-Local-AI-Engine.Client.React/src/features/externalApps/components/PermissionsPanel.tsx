import { Alert, List, Stack, Text } from "@mantine/core";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import type {
	ExternalAppEffectivePermissionsView,
	ExternalAppPermissionsView,
	ExternalAppServicePermissionsView,
} from "@/features/externalApps/models/ExternalAppModels";
import { externalAppPermissionNames } from "@/features/externalApps/models/ExternalAppModels";

interface PermissionsPanelProps {
	/** The manifest's own grants. Read only when `effectivePermissions` is absent. */
	readonly permissions?: ExternalAppPermissionsView;
	/**
	 * Both previews carry it; the instance overview does not. Supplied → the per-part block renders AND the standing
	 * host-files and graphics-card rows are read from it rather than from `permissions`, because it describes the
	 * manifest being consented to. An update that reads the INSTALLED manifest here renders a newly granted GPU as
	 * "no access" under "It does not get" while highlighting it as new — the disclosure contradicting itself.
	 */
	readonly effectivePermissions?: ExternalAppEffectivePermissionsView;
	/** Server-computed, per service, from the closed eight-name vocabulary. Never recomputed here. */
	readonly addedPermissions?: readonly string[];
	readonly "data-testid"?: string;
}

const keyPrefix = "pages.externalApps.permissions";

/**
 * The four names the three application-level blocks have no standing row for. As a DELTA each renders its own
 * `permissions.added.*` line inside the highlighted block; as a standing grant they come from the server's four
 * application-level aggregates and from each part's own members.
 */
const partOnlyNames = ["capabilities", "writableRootFilesystem", "publishedPorts", "extraHosts"] as const;

/**
 * Which of the four a grant bag holds. The bag is either ONE service's members or the server's application-level
 * aggregate, which have the same shape — so the panel never unions the services map to reach an application-level
 * verdict, it reads the aggregate the server computed.
 */
function grantedNames(grants: ExternalAppServicePermissionsView): readonly (typeof partOnlyNames)[number][] {
	return [
		(grants.capabilities ?? []).length > 0 ? ("capabilities" as const) : null,
		grants.writableRootFilesystem === true ? ("writableRootFilesystem" as const) : null,
		(grants.publishedPorts ?? []).length > 0 ? ("publishedPorts" as const) : null,
		(grants.extraHosts ?? []).length > 0 ? ("extraHosts" as const) : null,
	].filter((name) => name !== null);
}

/**
 * What an application gets, in an operator's words — never "container", "bind mount", "capability" or "seccomp".
 *
 * The network block states internet and local-network access as FACTS and never as a denial: V1 enforces no outbound
 * restriction, so a "denied" claim about the network would be a lie the UI tells on the node's behalf.
 */
export function PermissionsPanel({
	permissions,
	effectivePermissions,
	addedPermissions,
	"data-testid": testId,
}: PermissionsPanelProps) {
	const { t } = useTranslation();

	// A name outside the closed vocabulary highlights nothing and renders nothing: the SPA has no wording for a grant
	// it does not know, and inventing one would describe an access nobody wrote a sentence for.
	const added = new Set(
		(addedPermissions ?? []).filter((name) => (externalAppPermissionNames as readonly string[]).includes(name)),
	);
	// The grants being disclosed. `effectivePermissions` is the manifest under consent (the update's TARGET); the
	// `permissions` prop is the fallback for the catalog card and the instance overview, which carry no effective view.
	const source = effectivePermissions ?? permissions;
	const hostFiles = source?.hostFiles ?? "none";
	const gpu = source?.gpu ?? "none";
	// Every highlighted name, not only the four with no standing row: the alert is what tells an operator the update
	// widened access at all, and one that listed only four of the eight stayed silent for a newly granted GPU.
	const addedNames = externalAppPermissionNames.filter((name) => added.has(name));

	return (
		<Stack gap="md" data-testid={testId ?? "external-app-permissions"}>
			<Text fw={600}>{t(`${keyPrefix}.title`)}</Text>

			<Block title={t(`${keyPrefix}.networkTitle`)}>
				<Row name="internet" text={t(`${keyPrefix}.internet`)} added={added.has("internet")} />
				{/* Disclosure only: the sentence does not change with `localNetwork`, because V1 cannot switch it off. */}
				<Row name="localNetwork" text={t(`${keyPrefix}.localNetwork`)} added={added.has("localNetwork")} />
			</Block>

			<Block title={t(`${keyPrefix}.grantedTitle`)}>
				{hostFiles === "none" ? null : (
					<Row name="hostFiles" text={t(`${keyPrefix}.hostFiles.${hostFiles}`)} added={added.has("hostFiles")} />
				)}
				{gpu === "none" ? null : <Row name="gpu" text={t(`${keyPrefix}.gpu.${gpu}`)} added={added.has("gpu")} />}
				{/* The server's application-level aggregates: what the application as a whole holds beyond the standing rows. */}
				{effectivePermissions
					? grantedNames(effectivePermissions).map((name) => (
							<List.Item key={name} data-testid={`external-app-permission-effective-${name}`}>
								{t(`${keyPrefix}.added.${name}`)}
							</List.Item>
						))
					: null}
				<Row name="storage" text={t(`${keyPrefix}.storage`)} added={false} />
			</Block>

			{/* Only enforced claims belong here. Every line is something the node actually prevents. */}
			<Block title={t(`${keyPrefix}.deniedTitle`)}>
				{hostFiles === "none" ? (
					<Row name="hostFiles" text={t(`${keyPrefix}.hostFiles.none`)} added={added.has("hostFiles")} />
				) : null}
				{gpu === "none" ? <Row name="gpu" text={t(`${keyPrefix}.gpu.none`)} added={added.has("gpu")} /> : null}
				<Row name="noXeData" text={t(`${keyPrefix}.noXeData`)} added={false} />
				<Row name="noDevices" text={t(`${keyPrefix}.noDevices`)} added={false} />
				<Row name="loopbackOnly" text={t(`${keyPrefix}.loopbackOnly`)} added={false} />
			</Block>

			{addedNames.length > 0 ? (
				<Alert color="yellow" data-testid="external-app-permissions-added">
					<Stack gap={4}>
						<Text size="sm" fw={600}>
							{t(`${keyPrefix}.changedTitle`)}
						</Text>
						<Text size="sm">{t(`${keyPrefix}.changedDescription`)}</Text>
						<List size="sm">
							{addedNames.map((name) => (
								<List.Item key={name} data-added="true" data-testid={`external-app-permission-added-${name}`}>
									{t(`${keyPrefix}.added.${name}`)}
								</List.Item>
							))}
						</List>
					</Stack>
				</Alert>
			) : null}

			{effectivePermissions ? (
				<Block title={t(`${keyPrefix}.effective.title`)} testId="external-app-permissions-effective">
					{Object.entries(effectivePermissions.services ?? {}).map(([service, grants]) => (
						<PartBlock key={service} service={service} grants={grants} />
					))}
				</Block>
			) : null}
		</Stack>
	);
}

function Block({ title, testId, children }: { title: string; testId?: string; children: ReactNode }) {
	return (
		<Stack gap={4} data-testid={testId}>
			<Text size="sm" fw={600}>
				{title}
			</Text>
			<List size="sm">{children}</List>
		</Stack>
	);
}

/**
 * One disclosure line. `added` marks it as new access this update asks for. The four names with no standing row carry
 * the marker on their `external-app-permission-added-*` line in the alert instead; the other four carry it in both
 * places, which is why the two test ids differ.
 */
function Row({ name, text, added }: { name: string; text: string; added: boolean }) {
	return (
		<List.Item
			c={added ? "orange" : undefined}
			fw={added ? 600 : undefined}
			data-added={added ? "true" : undefined}
			data-testid={`external-app-permission-${name}`}
		>
			{text}
		</List.Item>
	);
}

/**
 * One part of the application and what it alone gets. The three blocks above are application-level and hide a
 * privilege a single part holds; this is the block that shows it.
 */
function PartBlock({ service, grants }: { service: string; grants: ExternalAppServicePermissionsView }) {
	const { t } = useTranslation();
	const items = grantedNames(grants).map((name) => t(`${keyPrefix}.added.${name}`));

	return (
		<List.Item data-testid={`external-app-permission-part-${service}`}>
			<Text size="sm" fw={500}>
				{t(`${keyPrefix}.effective.part`, { name: service })}
			</Text>
			<List size="sm" withPadding={true}>
				{items.length === 0 ? (
					<List.Item>{t(`${keyPrefix}.effective.none`)}</List.Item>
				) : (
					items.map((item) => <List.Item key={item}>{item}</List.Item>)
				)}
			</List>
		</List.Item>
	);
}
