import { defaultsOf, validate } from './forms';
import { contextContract, inputContract } from '../test/catalog';

describe('contract forms', () => {
  it('fills only the defaults the contract declares, never inventing one', () => {
    expect(defaultsOf(inputContract)).toEqual({ label: 'x' });
    expect(validate(inputContract, defaultsOf(inputContract))).toEqual([{ field: 'days', message: 'days is required.' }]);
  });

  it('refuses a fractional integer and values outside the declared range', () => {
    expect(validate(inputContract, { days: 2.5 }).map((i) => i.message)).toEqual(['A whole number is required.']);
    expect(validate(inputContract, { days: 0 }).map((i) => i.message)).toEqual(['Must be at least 1.']);
    expect(validate(inputContract, { days: 32 }).map((i) => i.message)).toEqual(['Must be at most 31.']);
  });

  it('refuses a date that does not exist rather than rolling it over', () => {
    expect(validate(contextContract, { region: 'North', asOf: '2026-02-30' })).toEqual([{ field: 'asOf', message: 'A date as yyyy-MM-dd is required.' }]);
    expect(validate(contextContract, { region: 'North', asOf: '2028-02-29' })).toEqual([]);
  });

  it('reports a field the contract does not have instead of passing it on', () => {
    expect(validate(inputContract, { days: 3, extra: 1 })).toEqual([{ field: 'extra', message: 'test.input has no field extra.' }]);
  });
});
