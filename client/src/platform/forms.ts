import type { ContractDefinition, FieldSchema } from './contracts';

// Form values against a contract's schema. The server checks the same schema and is the authority;
// this check only tells the user early, with the same paths.

export interface FieldIssue {
  readonly field: string;
  readonly message: string;
}

export function defaultsOf(contract: ContractDefinition): Record<string, unknown> {
  const values: Record<string, unknown> = {};
  for (const [name, field] of Object.entries(contract.schema.properties))
    if (field.default !== undefined) values[name] = field.default;
  return values;
}

export function fieldProblem(field: FieldSchema, value: unknown): string | undefined {
  switch (field.type) {
    case 'string': {
      if (typeof value !== 'string') return 'A text value is required.';
      if (field.minLength !== undefined && value.length < field.minLength) return `At least ${field.minLength} characters are required.`;
      if (field.enum !== undefined && !field.enum.includes(value)) return `Must be one of ${field.enum.join(', ')}.`;
      if (field.format === 'date' && !isIsoDate(value)) return 'A date as yyyy-MM-dd is required.';
      return undefined;
    }
    case 'integer':
    case 'number': {
      if (typeof value !== 'number' || !Number.isFinite(value)) return 'A number is required.';
      if (field.type === 'integer' && !Number.isInteger(value)) return 'A whole number is required.';
      if (field.minimum !== undefined && value < field.minimum) return `Must be at least ${field.minimum}.`;
      if (field.maximum !== undefined && value > field.maximum) return `Must be at most ${field.maximum}.`;
      return undefined;
    }
    case 'boolean':
      return typeof value === 'boolean' ? undefined : 'true or false is required.';
  }
}

export function validate(contract: ContractDefinition, values: Readonly<Record<string, unknown>>): FieldIssue[] {
  const issues: FieldIssue[] = [];
  for (const required of contract.schema.required ?? [])
    if (values[required] === undefined) issues.push({ field: required, message: `${required} is required.` });
  for (const [name, value] of Object.entries(values)) {
    const field = contract.schema.properties[name];
    if (field === undefined) {
      issues.push({ field: name, message: `${contract.contractId} has no field ${name}.` });
      continue;
    }
    if (value === undefined) continue;
    const problem = fieldProblem(field, value);
    if (problem !== undefined) issues.push({ field: name, message: problem });
  }
  return issues;
}

function isIsoDate(text: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(text)) return false;
  const date = new Date(`${text}T00:00:00Z`);
  return !Number.isNaN(date.getTime()) && date.toISOString().startsWith(text);
}
