import { useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import { submitWorkflow } from '../platform/actions';
import { defaultsOf, validate } from '../platform/forms';
import { useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { keyOf } from '../platform/workspace/workspaceStore';
import { ContractFields } from './ContractForm';

/** The input form of the workflow the palette chose. Enter submits; Escape closes. */
export function RunDialog() {
  const workspace = useWorkspaceContext();
  const workflowKey = useWorkspace((s) => s.runDialog);
  const close = useWorkspace((s) => s.openRunDialog);
  const workflow = workspace.catalog.workflows.find((w) => keyOf(w) === workflowKey);
  return (
    <Modal opened={workflow !== undefined} onClose={() => close(undefined)} title={workflow?.title ?? ''} trapFocus returnFocus transitionProps={{ duration: 0 }}>
      {workflow !== undefined && <RunForm key={workflowKey} workflowKey={keyOf(workflow)} onDone={() => close(undefined)} />}
    </Modal>
  );
}

function RunForm({ workflowKey, onDone }: { readonly workflowKey: string; readonly onDone: () => void }) {
  const workspace = useWorkspaceContext();
  const prepared = useWorkspace((s) => s.context.prepared);
  const workflow = workspace.catalog.workflows.find((w) => keyOf(w) === workflowKey)!;
  const contract = workspace.catalog.contracts.find((c) => c.contractId === workflow.inputContractId && c.contractVersion === workflow.inputContractVersion);
  if (contract === undefined) throw new Error(`The input contract of ${workflowKey} is not in the catalog.`);
  const [values, setValues] = useState<Record<string, unknown>>(() => defaultsOf(contract));
  const [submitting, setSubmitting] = useState(false);
  // Errors show once a field was changed or a submit was tried, not on a form the user has not touched.
  const [shown, setShown] = useState<ReadonlySet<string> | 'all'>(new Set());
  // One key per opened form: a retried submit of the same values returns the same Run.
  const [idempotencyKey] = useState(() => crypto.randomUUID());
  // The first field takes the keyboard as the form mounts, not on a later timer.
  const form = useRef<HTMLFormElement>(null);
  useLayoutEffect(() => { form.current?.querySelector<HTMLElement>('[data-autofocus]')?.focus({ preventScroll: true }); }, []);
  const issues = useMemo(() => validate(contract, values), [contract, values]);
  const errors = Object.fromEntries(issues.filter((i) => shown === 'all' || shown.has(i.field)).map((i) => [i.field, i.message]));
  const blocked = prepared === undefined ? 'The context is not valid.' : issues.length > 0 ? 'Fix the highlighted fields.' : undefined;

  const submit = async () => {
    setShown('all');
    if (blocked !== undefined || prepared === undefined || submitting) return;
    setSubmitting(true);
    const ok = await submitWorkflow(workspace, workflowKey, values, prepared, idempotencyKey);
    setSubmitting(false);
    if (ok) onDone();
  };

  return (
    <form ref={form} onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <Stack gap="sm">
        <ContractFields contract={contract} values={values} errors={errors}
          onChange={(field, value) => {
            setValues((v) => ({ ...v, [field]: value }));
            setShown((s) => (s === 'all' ? s : new Set([...s, field])));
          }} autoFocus />
        <Text size="xs" c="dimmed">
          Context: {prepared === undefined ? 'invalid' : Object.entries(prepared).map(([k, v]) => `${k} ${String(v)}`).join(' · ')}
        </Text>
        <Group justify="flex-end">
          <Button type="submit" loading={submitting} {...(blocked === undefined ? {} : { title: blocked })}>Run</Button>
        </Group>
      </Stack>
    </form>
  );
}
