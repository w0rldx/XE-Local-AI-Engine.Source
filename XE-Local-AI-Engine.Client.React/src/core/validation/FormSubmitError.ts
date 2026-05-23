export interface IFormSubmitError<TFieldName extends string = string> {
	form?: string;
	fields?: Partial<Record<TFieldName, string>>;
}
