// Prefilled GitHub bug report link.
//
// Only fields that exist in `.github/ISSUE_TEMPLATE/bug_report.yml` are set (`version`, `os`, `browser`, `hardware`);
// GitHub ignores unknown ids silently, so a renamed template field would just stop being prefilled. The `os` value must
// equal one of the template's dropdown labels exactly. Nothing is sent: the link only opens in a new tab.

import type {
	XeLocalAiEngineClientEndpointsDiagnosticsV1NodeInfoGpuResponse,
	XeLocalAiEngineClientEndpointsDiagnosticsV1NodeInfoResponse,
} from "@/core/api/generated";
import type { Snapshot } from "@/core/diagnostics/Diagnostics";

/** The node-info fields the link reads. */
export type IssueNodeInfo = Pick<
	XeLocalAiEngineClientEndpointsDiagnosticsV1NodeInfoResponse,
	"version" | "repositoryUrl" | "osDescription" | "cpuModel" | "cpuCores" | "totalRamBytes"
> & {
	readonly gpus: readonly Pick<XeLocalAiEngineClientEndpointsDiagnosticsV1NodeInfoGpuResponse, "name" | "totalBytes">[] | null;
};

export type IssueOs = "Windows 11" | "Windows 10" | "Linux" | "Other";

const TITLE_MAX = 80;
const GIB = 1024 ** 3;
// Windows 11 still reports kernel 10.0; build 22000 is its first release.
const WINDOWS_11_FIRST_BUILD = 22_000;
const LINUX_PATTERN =
	/\b(linux|ubuntu|debian|fedora|arch|manjaro|mint|opensuse|suse|centos|red hat|rhel|alpine|pop!_os|nixos)\b/i;

/** Maps .NET's `RuntimeInformation.OSDescription` to one of the template's dropdown labels. */
export function mapIssueOs(osDescription: string): IssueOs {
	if (/windows/i.test(osDescription)) {
		const build = /10\.0\.(\d+)/.exec(osDescription)?.[1];
		return build !== undefined && Number(build) >= WINDOWS_11_FIRST_BUILD ? "Windows 11" : "Windows 10";
	}
	return LINUX_PATTERN.test(osDescription) ? "Linux" : "Other";
}

/** `Edge 128` / `Chrome 129` / `Firefox 130` / `Safari 17` out of a user agent, or undefined when unrecognised. */
export function browserFamily(userAgent: string): string | undefined {
	const families: readonly [string, RegExp][] = [
		["Edge", /Edg(?:e|A|iOS)?\/(\d+)/],
		["Opera", /OPR\/(\d+)/],
		["Firefox", /Firefox\/(\d+)/],
		["Chrome", /Chrom(?:e|ium)\/(\d+)/],
		["Safari", /Version\/(\d+)[^ ]* (?:Mobile\/\S+ )?Safari\//],
	];
	for (const [name, pattern] of families) {
		const major = pattern.exec(userAgent)?.[1];
		if (major !== undefined) {
			return `${name} ${major}`;
		}
	}
	return undefined;
}

function gib(bytes: number): string {
	return `${Math.round(bytes / GIB)} GB`;
}

/** `CPU: … / GPU: … (… GB) / RAM: … GB`, leaving out whatever the node could not read. */
export function describeHardware(nodeInfo: IssueNodeInfo): string {
	const parts: string[] = [];
	if (nodeInfo.cpuModel) {
		parts.push(`CPU: ${nodeInfo.cpuModel}${nodeInfo.cpuCores ? ` (${nodeInfo.cpuCores} cores)` : ""}`);
	}
	const gpus = (nodeInfo.gpus ?? []).map((gpu) => (gpu.totalBytes ? `${gpu.name} (${gib(gpu.totalBytes)})` : gpu.name));
	if (gpus.length > 0) {
		parts.push(`GPU: ${gpus.join(", ")}`);
	}
	if (nodeInfo.totalRamBytes) {
		parts.push(`RAM: ${gib(nodeInfo.totalRamBytes)}`);
	}
	return parts.join(" / ");
}

/** The prefilled `issues/new` link, or undefined when the build carries no https repository URL. */
export function buildIssueUrl(nodeInfo: IssueNodeInfo, snapshot?: Snapshot, userAgent = navigator.userAgent): string | undefined {
	const repositoryUrl = nodeInfo.repositoryUrl?.replace(/\/+$/, "");
	if (!repositoryUrl?.startsWith("https://")) {
		return undefined;
	}
	const message = snapshot?.error?.message.trim();
	const fields: Record<string, string | undefined> = {
		template: "bug_report.yml",
		title: message ? message.slice(0, TITLE_MAX) : "Problem report",
		version: nodeInfo.version,
		os: mapIssueOs(nodeInfo.osDescription),
		browser: browserFamily(userAgent),
		hardware: describeHardware(nodeInfo) || undefined,
	};
	const query = Object.entries(fields)
		.filter((entry): entry is [string, string] => entry[1] !== undefined)
		.map(([key, value]) => `${key}=${encodeURIComponent(value)}`)
		.join("&");
	return `${repositoryUrl}/issues/new?${query}`;
}
