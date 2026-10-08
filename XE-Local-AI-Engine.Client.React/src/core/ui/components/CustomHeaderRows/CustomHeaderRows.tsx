import { ActionIcon, Button, Flex, Group, PasswordInput, Stack, Switch, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { type ChangeEvent, Fragment, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

import type { CustomHeaderDraft, CustomHeaderError } from "@/core/http-headers/CustomHeaders";

interface CustomHeaderRowsProps {
	readonly headers: readonly CustomHeaderDraft[];
	readonly rowIds: readonly string[];
	readonly title: ReactNode;
	readonly description: ReactNode;
	readonly namePlaceholder: string;
	readonly error?: CustomHeaderError;
	/** Prefix of every `data-testid`, so each feature keeps its own stable ids. */
	readonly testIdPrefix: string;
	readonly onAdd: () => void;
	readonly onRemove: (index: number) => void;
	readonly onChange: (index: number, field: "name" | "value", value: string) => void;
	readonly onToggleSecret: (index: number) => void;
	readonly onBlur: () => void;
}

// Presentational header-row editor: name, value (masked when secret), a Secret switch and a remove button per row. A
// row-level error renders under its row; a list-level one (the row cap) under the list.
export function CustomHeaderRows(props: CustomHeaderRowsProps) {
	const { t } = useTranslation();
	const { headers, rowIds, error, testIdPrefix: prefix, onChange, onBlur } = props;
	const nameLabel = t("components.customHeaders.nameLabel", "Header name");
	const valueLabel = t("components.customHeaders.valueLabel", "Value");
	const secretLabel = t("components.customHeaders.secretLabel", "Secret");

	return (
		<Stack gap={6}>
			<Text size="sm" fw={500}>
				{props.title}
			</Text>
			<Text size="xs" c="dimmed">
				{props.description}
			</Text>
			{headers.map((header, index) => {
				const rowError = error?.index === index ? error.message : undefined;
				const valueProps = {
					style: { flex: "1 1 auto", minWidth: 0 },
					"aria-label": valueLabel,
					label: index === 0 ? valueLabel : undefined,
					value: header.value,
					"data-testid": `${prefix}-header-value-${index}`,
					onChange: (event: ChangeEvent<HTMLInputElement>) => onChange(index, "value", event.currentTarget.value),
					onBlur,
				};
				return (
					<Fragment key={rowIds[index]}>
						<Group align="flex-end" gap="xs" wrap="nowrap">
							<Flex
								direction={{ base: "column", sm: "row" }}
								gap="xs"
								align={{ base: "stretch", sm: "flex-end" }}
								style={{ flex: "1 1 auto", minWidth: 0 }}
							>
								<TextInput
									style={{ flex: "1 1 auto", minWidth: 0 }}
									aria-label={nameLabel}
									label={index === 0 ? nameLabel : undefined}
									placeholder={props.namePlaceholder}
									value={header.name}
									error={rowError !== undefined}
									data-testid={`${prefix}-header-name-${index}`}
									onChange={(event) => onChange(index, "name", event.currentTarget.value)}
									onBlur={onBlur}
								/>
								{header.isSecret ? (
									<PasswordInput
										{...valueProps}
										description={header.hasStoredSecret ? t("components.customHeaders.secretStoredHint") : undefined}
										placeholder={header.hasStoredSecret ? "••••••••" : t("components.customHeaders.valuePlaceholder", "value")}
									/>
								) : (
									<TextInput {...valueProps} placeholder={t("components.customHeaders.valuePlaceholder", "value")} />
								)}
								<Switch
									data-testid={`${prefix}-header-secret-${index}`}
									aria-label={secretLabel}
									label={index === 0 ? secretLabel : undefined}
									checked={header.isSecret}
									onChange={() => props.onToggleSecret(index)}
									style={{ flex: "0 0 auto" }}
								/>
							</Flex>
							<ActionIcon
								variant="subtle"
								color="red"
								size="lg"
								data-testid={`${prefix}-remove-header-${index}`}
								aria-label={t("components.customHeaders.removeHeader", "Remove header")}
								onClick={() => props.onRemove(index)}
							>
								<IconTrash size={16} />
							</ActionIcon>
						</Group>
						{rowError !== undefined ? (
							<Text size="xs" c="red" data-testid={`${prefix}-header-error-${index}`}>
								{rowError}
							</Text>
						) : null}
					</Fragment>
				);
			})}
			{error !== undefined && error.index === undefined ? (
				<Text size="xs" c="red" data-testid={`${prefix}-headers-error`}>
					{error.message}
				</Text>
			) : null}
			<Group>
				<Button
					variant="light"
					size="xs"
					leftSection={<IconPlus size={14} />}
					data-testid={`${prefix}-add-header`}
					onClick={props.onAdd}
				>
					{t("components.customHeaders.addHeader", "Add header")}
				</Button>
			</Group>
		</Stack>
	);
}
