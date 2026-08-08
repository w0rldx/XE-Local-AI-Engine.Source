export interface AccessibilityIssue {
	rule: string;
	element: Element;
}

export function filterNewAccessibilityIssues(
	issues: AccessibilityIssue[],
	previous: ReadonlyMap<Element, string>,
): { additions: AccessibilityIssue[]; current: Map<Element, string> } {
	const current = new Map(issues.map((issue) => [issue.element, issue.rule]));
	return { additions: issues.filter((issue) => previous.get(issue.element) !== issue.rule), current };
}

function isHidden(element: Element): boolean {
	return element.closest('[hidden], [aria-hidden="true"]') !== null;
}

function hasAccessibleName(element: Element): boolean {
	const ariaLabel = element.getAttribute("aria-label")?.trim();
	if (ariaLabel) {
		return true;
	}

	const labelledBy = element.getAttribute("aria-labelledby")?.trim().split(/\s+/) ?? [];
	if (labelledBy.some((id) => document.getElementById(id)?.textContent?.trim())) {
		return true;
	}

	return Boolean(element.textContent?.trim());
}

function hasFormLabel(element: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement): boolean {
	return element.labels !== null && element.labels.length > 0;
}

export function findAccessibilityIssues(root: ParentNode = document): AccessibilityIssue[] {
	const issues: AccessibilityIssue[] = [];
	for (const image of root.querySelectorAll("img:not([alt])")) {
		if (!isHidden(image)) {
			issues.push({ rule: "Images need an alt attribute (use alt=\"\" for decorative images).", element: image });
		}
	}

	for (const control of root.querySelectorAll("button, a[href]")) {
		if (!isHidden(control) && !hasAccessibleName(control)) {
			issues.push({ rule: "Interactive controls need an accessible name.", element: control });
		}
	}

	for (const field of root.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(
		"input:not([type=hidden]), select, textarea",
	)) {
		if (!isHidden(field) && !hasFormLabel(field) && !hasAccessibleName(field) && !field.title.trim()) {
			issues.push({ rule: "Form controls need a label, aria-label, or aria-labelledby.", element: field });
		}
	}

	return issues;
}

export function installDevelopmentAccessibilityAudit(root: ParentNode = document): () => void {
	let idleHandle: number | undefined;
	let previous = new Map<Element, string>();
	const audit = (): void => {
		idleHandle = undefined;
		const result = filterNewAccessibilityIssues(findAccessibilityIssues(root), previous);
		previous = result.current;
		if (result.additions.length === 0) {
			return;
		}
		// biome-ignore lint/suspicious/noConsole: This development-only audit deliberately reports findings in browser tools.
		console.groupCollapsed(
			`[accessibility] ${result.additions.length} new potential issue${result.additions.length === 1 ? "" : "s"}`,
		);
		for (const issue of result.additions) {
			console.warn(issue.rule, issue.element);
		}
		// biome-ignore lint/suspicious/noConsole: Closes the development-only browser-tools report group above.
		console.groupEnd();
	};
	const schedule = (): void => {
		if (idleHandle !== undefined) {
			if (typeof window.cancelIdleCallback === "function") {
				window.cancelIdleCallback(idleHandle);
			} else {
				window.clearTimeout(idleHandle);
			}
		}
		idleHandle =
			typeof window.requestIdleCallback === "function"
				? window.requestIdleCallback(audit, { timeout: 1_000 })
				: window.setTimeout(audit, 250);
	};

	const observer = new MutationObserver(schedule);
	observer.observe(root === document ? document.documentElement : root, {
		childList: true,
		characterData: true,
		subtree: true,
		attributes: true,
		attributeFilter: ["alt", "aria-hidden", "aria-label", "aria-labelledby", "for", "hidden", "href", "id", "title", "type"],
	});
	schedule();
	return () => {
		observer.disconnect();
		if (idleHandle !== undefined) {
			if (typeof window.cancelIdleCallback === "function") {
				window.cancelIdleCallback(idleHandle);
			} else {
				window.clearTimeout(idleHandle);
			}
		}
	};
}
