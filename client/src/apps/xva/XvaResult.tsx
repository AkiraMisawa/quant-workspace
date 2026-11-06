import { Alert, Badge, Button, Group, Stack, Table, Text } from '@mantine/core';
import { useDataset, useRun } from '../../platform/workspace/WorkspaceProvider';

// The latest Price's figures, on the workspace page itself: CVA and FVA Before, After and incremental. They are read
// from the Run's retained result, never recomputed here, and say what they are: simulated, of which revision, and
// whether the workspace has moved on since.

const money = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const SIDES = ['before', 'after', 'incremental'] as const;
const SIDE_TITLES: Readonly<Record<(typeof SIDES)[number], string>> = { before: 'Before', after: 'After', incremental: 'Incremental' };

/** A figure as the result holds it. A missing one is unavailable, never zero (EX06). */
export function figure(value: unknown): string {
  return typeof value === 'number' && Number.isFinite(value) ? money.format(value) : 'unavailable';
}

export function XvaResult({ runId, pricedRevision, revision, onOpen }: {
  readonly runId: string;
  /** The workspace revision the Run froze; unknown until the workspace lists the Run. */
  readonly pricedRevision: number | undefined;
  readonly revision: number;
  readonly onOpen: () => void;
}) {
  const run = useRun(runId);
  const reference = run.data?.outputs.result;
  const result = useDataset(reference);

  if (run.isError) return <Alert color="red" variant="light" title="Result">{run.error.message}</Alert>;
  const status = run.data?.status ?? 'running';
  const rows = result.data?.rows ?? [];
  const tradesBefore = rows[0]?.tradesBefore;
  const tradesAfter = rows[0]?.tradesAfter;
  const behind = pricedRevision !== undefined && pricedRevision < revision;

  return (
    <Stack gap={4} className="qwb-xva-result" data-testid="xva-result" aria-label="Latest price">
      <Group gap="xs" wrap="wrap">
        <Text size="sm" fw={600}>Latest price</Text>
        <Badge variant="light" color={status === 'succeeded' ? 'teal' : status === 'running' ? 'blue' : status === 'failed' ? 'red' : 'yellow'}
          data-testid="xva-result-status">{status}</Badge>
        {result.data?.provenance.executionMode === 'simulated' && <Badge variant="light" color="yellow">simulated</Badge>}
        {pricedRevision !== undefined && (
          <Badge variant="light" color={behind ? 'yellow' : 'gray'} data-testid="xva-result-revision"
            title={behind ? 'The workspace changed after this Price. Price again to see its figures.' : 'The figures are of the workspace as it stands.'}>
            {behind ? `revision ${pricedRevision} · workspace now ${revision}` : `revision ${pricedRevision}`}
          </Badge>
        )}
        {typeof tradesBefore === 'number' && typeof tradesAfter === 'number' && (
          <Text size="xs" c="dimmed">{tradesBefore} trade(s) before · {tradesAfter} after</Text>
        )}
        <Button size="compact-xs" variant="default" ml="auto" onClick={onOpen}>Open run</Button>
      </Group>
      {status === 'failed' && <Alert color="red" variant="light" title={run.data?.error?.code ?? 'Failed'}>{run.data?.error?.message}</Alert>}
      {result.isError && <Alert color="red" variant="light" title="The result could not be read">{result.error.message}</Alert>}
      {rows.length > 0 && (
        <div className="qwb-xva-result-table">
          <Table withTableBorder withColumnBorders verticalSpacing={4} horizontalSpacing="sm" fz="sm" aria-label="CVA and FVA">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Measure</Table.Th>
                {SIDES.map((side) => <Table.Th key={side} className="qwb-number">{SIDE_TITLES[side]}</Table.Th>)}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((row) => (
                <Table.Tr key={String(row.measure)}>
                  <Table.Td>{String(row.measure)}</Table.Td>
                  {SIDES.map((side) => <Table.Td key={side} className="qwb-number">{figure(row[side])}</Table.Td>)}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </div>
      )}
    </Stack>
  );
}
