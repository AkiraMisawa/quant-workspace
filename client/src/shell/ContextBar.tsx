import { Group, Text } from '@mantine/core';
import { CONTEXT_CONTRACT, useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { ContractFields } from './ContractForm';

/**
 * The workspace's context. A change takes effect for the next submission only once it is valid; an
 * invalid request leaves the last prepared context in force and says so.
 */
export function ContextBar() {
  const { catalog } = useWorkspaceContext();
  const contract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT)!;
  const context = useWorkspace((s) => s.context);
  const requestContext = useWorkspace((s) => s.requestContext);
  const errors = Object.fromEntries(context.issues.map((i) => [i.field, i.message]));
  const pending = context.issues.length > 0;
  return (
    <Group gap="xs" align="flex-end" wrap="wrap" aria-label="Context" role="group" id="context-bar">
      <ContractFields contract={contract} values={context.requested} errors={errors}
        onChange={(field, value) => requestContext(contract, { [field]: value })} compact />
      {pending && (
        <Text size="xs" c="yellow" data-testid="context-pending">
          Using the previous context until this one is valid.
        </Text>
      )}
    </Group>
  );
}
