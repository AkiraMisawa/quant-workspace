import { useCallback } from 'react';
import { Alert, Badge, Group, SegmentedControl, Stack, Text } from '@mantine/core';
import type { RunRecord } from '../platform/contracts';
import { viewsFor } from '../platform/views/binding';
import { ViewRenderer } from '../platform/views/ViewRenderer';
import { useRun, useWorkspace, useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { keyOf, type Panel } from '../platform/workspace/workspaceStore';
import { registerGridFocus } from './focus';

const statusColour: Record<RunRecord['status'], string> = {
  running: 'blue',
  succeeded: 'teal',
  partiallySucceeded: 'yellow',
  failed: 'red',
};

/** A Run in a panel: its state, what it ran against, and any registered view of its outputs. */
export function RunPanel({ panel }: { readonly panel: Extract<Panel, { kind: 'run' }> }) {
  const { catalog } = useWorkspaceContext();
  const setPanelView = useWorkspace((s) => s.setPanelView);
  const run = useRun(panel.runId);
  const registerFocus = useCallback((focus: (() => void) | undefined) => registerGridFocus(panel.id, focus), [panel.id]);

  if (run.isPending) return <Text c="dimmed">Loading run…</Text>;
  if (run.isError) return <Alert color="red" title="Run">{run.error.message}</Alert>;
  const record = run.data;
  const views = viewsFor(record.workflow, catalog.views);
  const view = views.find((v) => keyOf(v) === panel.viewKey);
  const failed = record.invocations.filter((i) => i.status === 'failed' || i.status === 'blocked');

  return (
    <Stack gap="sm">
      <Group gap="xs" wrap="wrap">
        <Badge color={statusColour[record.status]} variant="light" data-testid="run-status">{record.status}</Badge>
        <Text size="xs" c="dimmed" ff="monospace">{record.runId}</Text>
        <Text size="xs" c="dimmed">
          {Object.entries(record.context).map(([k, v]) => `${k} ${String(v)}`).join(' · ')}
          {' · '}
          {Object.entries(record.input).map(([k, v]) => `${k} ${String(v)}`).join(' · ')}
        </Text>
      </Group>
      {failed.length > 0 && (
        <Alert color="yellow" variant="light" title={`${failed.length} invocation(s) did not succeed`}>
          {failed.map((i) => <Text key={i.invocationKey} size="xs">{i.invocationKey}: {i.error?.message}</Text>)}
        </Alert>
      )}
      {record.status === 'failed' && record.error !== undefined && <Alert color="red" title={record.error.code}>{record.error.message}</Alert>}
      {record.status !== 'running' && views.length > 1 && (
        <SegmentedControl size="xs" aria-label="View" value={panel.viewKey ?? ''}
          data={views.map((v) => ({ value: keyOf(v), label: v.title }))}
          onChange={(value) => setPanelView(panel.id, value)} />
      )}
      {record.status === 'running' && <Text size="sm" c="dimmed">Running {record.invocations.length} invocation(s)…</Text>}
      {record.status !== 'running' && view === undefined && (
        <Text size="sm" c="dimmed">This Run has no view attached. Its outputs are retained; open a view from the palette.</Text>
      )}
      {record.status !== 'running' && view !== undefined && Object.keys(record.outputs).length > 0 && (
        <ViewRenderer view={view} outputs={record.outputs} registerFocus={registerFocus} />
      )}
    </Stack>
  );
}
