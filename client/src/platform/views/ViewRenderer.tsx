import { useCallback } from 'react';
import { Alert, Badge, Group, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import type { Dataset, TypedDataRef, ViewAction, ViewComponent, ViewDefinition } from '../contracts';
import { ApiError } from '../api';
import { DatasetGrid } from '../grid/DatasetGrid';
import { useDataset, useWorkspaceContext, useWorkspace } from '../workspace/WorkspaceProvider';
import { actionParameters, referenceOf } from './binding';
import { invokeAction } from '../actions';

// Renders a validated declarative view through approved components only (grid, details). A kind
// without a renderer is shown as refused, never approximated.

export function ViewRenderer({ view, outputs, registerFocus }: {
  readonly view: ViewDefinition;
  readonly outputs: Readonly<Record<string, TypedDataRef>>;
  readonly registerFocus?: (focus: (() => void) | undefined) => void;
}) {
  return (
    <SimpleGrid cols={{ base: view.layout.narrowColumns, md: view.layout.wideColumns }} spacing="md">
      {view.components.map((component, index) => (
        <ComponentRenderer key={component.id} component={component} outputs={outputs}
          {...(index === 0 && registerFocus !== undefined ? { registerFocus } : {})} />
      ))}
    </SimpleGrid>
  );
}

function ComponentRenderer({ component, outputs, registerFocus }: {
  readonly component: ViewComponent;
  readonly outputs: Readonly<Record<string, TypedDataRef>>;
  readonly registerFocus?: (focus: (() => void) | undefined) => void;
}) {
  const reference = referenceOf(component, outputs);
  const missing = 'missing' in reference ? reference.missing : undefined;
  const data = useDataset(missing === undefined ? (reference as TypedDataRef) : undefined);

  if (missing !== undefined)
    return <Alert color="red" title={component.title}>This Run produced no output named {missing}.</Alert>;
  if (data.isPending) return <Text c="dimmed" size="sm">Loading {component.title}…</Text>;
  if (data.isError) {
    const expired = data.error instanceof ApiError && data.error.status === 410;
    return (
      <Alert color={expired ? 'yellow' : 'red'} title={component.title}>
        {expired ? 'This result is no longer retained. It is not recomputed; run the workflow again to produce it.' : data.error.message}
      </Alert>
    );
  }
  return (
    <Stack gap={6}>
      <Group justify="space-between" wrap="nowrap">
        <Title order={4} size="sm">{component.title}</Title>
        <Provenance dataset={data.data} />
      </Group>
      {component.kind === 'grid'
        ? <GridComponent component={component} dataset={data.data} {...(registerFocus === undefined ? {} : { registerFocus })} />
        : component.kind === 'details'
          ? <Details dataset={data.data} />
          : <Alert color="red">Component kind {component.kind} has no approved renderer.</Alert>}
    </Stack>
  );
}

function GridComponent({ component, dataset, registerFocus }: {
  readonly component: ViewComponent;
  readonly dataset: Dataset;
  readonly registerFocus?: (focus: (() => void) | undefined) => void;
}) {
  const workspace = useWorkspaceContext();
  const prepared = useWorkspace((s) => s.context.prepared);
  const onAction = useCallback((action: ViewAction, row: Readonly<Record<string, unknown>>) => {
    const bound = actionParameters(action, row);
    if (!bound.ok) {
      workspace.notify(`${action.label} is not available for this row: it has no ${bound.missing.join(', ')}.`);
      return;
    }
    if (prepared === undefined) {
      workspace.notify('The context is not valid; fix it before running an action.');
      return;
    }
    void invokeAction(workspace, action, bound.parameters, prepared);
  }, [workspace, prepared]);

  return (
    <DatasetGrid
      label={component.title}
      dataset={dataset}
      columns={component.columns ?? []}
      actions={component.actions ?? []}
      onAction={onAction}
      {...(registerFocus === undefined ? {} : { registerFocus })}
    />
  );
}

function Details({ dataset }: { readonly dataset: Dataset }) {
  const row = dataset.rows[0];
  if (row === undefined) return <Text c="dimmed" size="sm">No rows.</Text>;
  return (
    <Stack gap={2}>
      {dataset.columns.map((c) => (
        <Group key={c.field} gap="xs"><Text size="sm" c="dimmed" w={120}>{c.field}</Text><Text size="sm">{String(row[c.field] ?? '')}</Text></Group>
      ))}
    </Stack>
  );
}

/** Says where numbers came from: synthetic data and simulated execution are labelled, always. */
function Provenance({ dataset }: { readonly dataset: Dataset }) {
  const { executionMode, dataOrigin } = dataset.provenance;
  return (
    <Group gap={4}>
      {dataOrigin !== 'source' && <Badge size="xs" variant="outline" color="yellow">{dataOrigin} data</Badge>}
      {executionMode === 'simulated' && <Badge size="xs" variant="outline" color="yellow">simulated</Badge>}
    </Group>
  );
}
