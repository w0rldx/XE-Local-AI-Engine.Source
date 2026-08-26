import { Group, Select, Stack, Textarea, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { CustomToolEditorSectionProps } from "@/features/customTools/components/CustomToolEditorShared";
import { CustomToolHostList } from "@/features/customTools/components/CustomToolHostList";
import { CustomToolSecretRows } from "@/features/customTools/components/CustomToolSecretRows";
import { errorAt } from "@/features/customTools/models/CustomToolFormErrors";
import {
	CUSTOM_TOOL_HTTP_METHODS,
	type CustomToolFormValues,
	type CustomToolHeader,
} from "@/features/customTools/models/CustomToolModels";

// HttpFetch editor: method, URL template, headers (name/value/isSecret), body template, allowedHosts.
export function HttpEditor({ values, errors, update }: CustomToolEditorSectionProps) {
	const { t } = useTranslation();
	const http = values.http;

	const patchHttp = (patch: Partial<CustomToolFormValues["http"]>) =>
		update((current) => ({ ...current, http: { ...current.http, ...patch } }));

	const addHeader = () => patchHttp({ headers: [...http.headers, { name: "", value: "", isSecret: false }] });
	const removeHeader = (index: number) => patchHttp({ headers: http.headers.filter((_, i) => i !== index) });
	const patchHeader = (index: number, patch: Partial<CustomToolHeader>) =>
		patchHttp({ headers: http.headers.map((header, i) => (i === index ? { ...header, ...patch } : header)) });

	return (
		<Stack gap="sm" data-testid="custom-tool-form-http">
			<Group grow={true} align="flex-start">
				<Select
					label={t("pages.customTools.form.http.method", "Method")}
					value={http.method}
					data={CUSTOM_TOOL_HTTP_METHODS.map((method) => ({ label: method, value: method }))}
					onChange={(value) => patchHttp({ method: value ?? "GET" })}
					allowDeselect={false}
					maw={140}
					data-testid="custom-tool-form-http-method"
				/>
			</Group>
			<TextInput
				label={t("pages.customTools.form.http.url", "URL template")}
				description={t(
					"pages.customTools.form.http.urlHint",
					"Use {param} placeholders for query values or path segments only — never the scheme, host, or port.",
				)}
				placeholder="https://api.example.com/weather?city={city}"
				value={http.urlTemplate}
				required={true}
				error={
					errorAt(errors, "http.urlTemplate")
						? t("pages.customTools.form.http.urlRequired", "A URL template is required.")
						: undefined
				}
				onChange={(event) => patchHttp({ urlTemplate: event.currentTarget.value })}
				data-testid="custom-tool-form-http-url"
			/>

			<CustomToolSecretRows
				title={t("pages.customTools.form.http.headers", "Headers")}
				addLabel={t("pages.customTools.form.http.addHeader", "Add header")}
				emptyLabel={t("pages.customTools.form.http.noHeaders", "No headers.")}
				testid="custom-tool-form-http-headers"
				rows={http.headers}
				onAdd={addHeader}
				onRemove={removeHeader}
				onPatch={patchHeader}
			/>

			<Textarea
				label={t("pages.customTools.form.http.body", "Body template")}
				description={t(
					"pages.customTools.form.http.bodyHint",
					"Optional request body. {param} placeholders are filled the same way.",
				)}
				value={http.bodyTemplate}
				autosize={true}
				minRows={2}
				onChange={(event) => patchHttp({ bodyTemplate: event.currentTarget.value })}
				data-testid="custom-tool-form-http-body"
			/>

			<CustomToolHostList value={http.allowedHosts} onChange={(allowedHosts) => patchHttp({ allowedHosts })} />
		</Stack>
	);
}
