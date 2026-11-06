import { MantineExGrid, type MantineExGridProps } from '@ex-grid/mantine';

/**
 * Every grid in the workbench: ExGrid drawn by its Mantine Wrapper (ADR-0001, docs/design-guidelines.md "Grids").
 * The Wrapper draws the grid's menus, filter panel and editor with Mantine's components, maps the theme's colours onto
 * ExGrid's Visual Tokens in both schemes, and draws the cells in the font whose widths ExGrid judges `####` by
 * (ex-grid ADR-0390 §9). The paper's look is decided here once: a square, bordered panel.
 */
export function WorkbenchGrid<Row>(props: MantineExGridProps<Row>) {
  return <MantineExGrid<Row> withBorder radius={0} {...props} />;
}
