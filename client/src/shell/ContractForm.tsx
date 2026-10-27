import { NumberInput, Select, Switch, TextInput } from '@mantine/core';
import type { ContractDefinition, FieldSchema } from '../platform/contracts';

/** Fields for a contract's schema, one approved input per field type. No field is invented. */
export function ContractFields({ contract, values, errors, onChange, autoFocus, compact }: {
  readonly contract: ContractDefinition;
  readonly values: Readonly<Record<string, unknown>>;
  readonly errors: Readonly<Record<string, string>>;
  readonly onChange: (field: string, value: unknown) => void;
  readonly autoFocus?: boolean;
  readonly compact?: boolean;
}) {
  return Object.entries(contract.schema.properties).map(([name, field], index) => (
    <Field key={name} name={name} field={field} value={values[name]} error={errors[name]}
      onChange={(v) => onChange(name, v)} autoFocus={autoFocus === true && index === 0} compact={compact === true} />
  ));
}

function Field({ name, field, value, error, onChange, autoFocus, compact }: {
  readonly name: string;
  readonly field: FieldSchema;
  readonly value: unknown;
  readonly error: string | undefined;
  readonly onChange: (value: unknown) => void;
  readonly autoFocus: boolean;
  readonly compact: boolean;
}) {
  const label = field.title ?? name;
  const common = { label, error, size: compact ? 'xs' : 'sm', 'data-autofocus': autoFocus || undefined } as const;
  if (field.type === 'string' && field.enum !== undefined)
    return <Select {...common} data={[...field.enum]} value={typeof value === 'string' ? value : null}
      onChange={(v) => onChange(v ?? undefined)} allowDeselect={false} comboboxProps={{ withinPortal: true }} />;
  if (field.type === 'string')
    return <TextInput {...common} type={field.format === 'date' ? 'date' : 'text'} value={typeof value === 'string' ? value : ''}
      onChange={(e) => onChange(e.currentTarget.value === '' ? undefined : e.currentTarget.value)} />;
  if (field.type === 'boolean')
    return <Switch label={label} checked={value === true} onChange={(e) => onChange(e.currentTarget.checked)} />;
  return <NumberInput {...common} value={typeof value === 'number' ? value : ''} allowDecimal={field.type === 'number'}
    {...(field.minimum === undefined ? {} : { min: field.minimum })} {...(field.maximum === undefined ? {} : { max: field.maximum })}
    onChange={(v) => onChange(typeof v === 'number' ? v : undefined)} />;
}
