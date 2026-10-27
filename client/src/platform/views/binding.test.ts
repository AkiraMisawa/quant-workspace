import { actionParameters, referenceOf, viewsFor } from './binding';
import { view, workflow } from '../../test/catalog';
import type { ViewAction } from '../contracts';

describe('view binding', () => {
  it('offers a view only when the workflow produces every output it reads', () => {
    const summary = view('v.summary', 'Summary', ['summary']);
    const both = view('v.both', 'Both', ['summary', 'readings']);
    expect(viewsFor(workflow('w', 'W', ['summary']), [summary, both])).toEqual([summary]);
  });

  it('reports a missing output by name instead of showing an empty grid', () => {
    const component = view('v', 'V', ['summary']).components[0]!;
    expect(referenceOf(component, {})).toEqual({ missing: 'summary' });
  });

  it('makes a row action unavailable when its bound field is null on that row', () => {
    const action: ViewAction = { actionId: 'a', label: 'Show readings', bindings: { readings: { kind: 'row', field: 'readings' } } } as ViewAction;
    expect(actionParameters(action, { readings: null })).toEqual({ ok: false, missing: ['readings'] });
    expect(actionParameters(action, { readings: 0 })).toEqual({ ok: true, parameters: { readings: 0 } });
  });
});
