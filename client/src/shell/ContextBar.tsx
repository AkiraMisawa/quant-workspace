import { Alert, Text } from '@mantine/core';
import { CONTEXT_CONTRACT, useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { ContractFields } from './ContractForm';

/**
 * The workspace's context, as toolbar fields in the header. A change takes effect for the next submission only once
 * it is valid; an invalid request leaves the last prepared context in force, and `ContextIssues` says so. The header
 * keeps one height whatever the fields hold (docs/design-guidelines.md, "The shell"), so the bar itself only marks an
 * invalid field.
 */
export function ContextBar({ className }: { readonly className?: string }) {
  const { catalog } = useWorkspaceContext();
  const contract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT)!;
  const context = useWorkspace((s) => s.context);
  const requestContext = useWorkspace((s) => s.requestContext);
  const errors = Object.fromEntries(context.issues.map((i) => [i.field, i.message]));
  return (
    <div aria-label="Context" role="group" id="context-bar" className={className}>
      <ContractFields contract={contract} values={context.requested} errors={errors}
        onChange={(field, value) => requestContext(contract, { [field]: value })} layout="toolbar" />
    </div>
  );
}

/** What is wrong with the context the user asked for, and that the previous one is still in force. */
export function ContextIssues() {
  const { catalog } = useWorkspaceContext();
  const contract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT)!;
  const issues = useWorkspace((s) => s.context.issues);
  if (issues.length === 0) return null;
  return (
    <Alert color="yellow" variant="light" mb="sm" role="status" data-testid="context-pending"
      title="Using the previous context until this one is valid.">
      {issues.map((i) => (
        <Text key={`${i.field}:${i.message}`} size="sm">
          {contract.schema.properties[i.field]?.title ?? i.field}: {i.message}
        </Text>
      ))}
    </Alert>
  );
}
