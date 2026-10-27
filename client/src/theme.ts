import { createTheme, type MantineColorsTuple } from '@mantine/core';

// Quant Workbench's own look: dense, dark-first, with a signal amber for focus and primary actions and
// tabular figures everywhere numbers line up. Mantine supplies the components; this supplies the system.

const signal: MantineColorsTuple = [
  '#fff6e0', '#ffeccb', '#ffd79a', '#ffc164', '#ffae36', '#ffa218', '#ff9c03', '#e38800', '#ca7800', '#af6700',
];

const ink: MantineColorsTuple = [
  '#eef1f6', '#d9dee7', '#b3bccc', '#8b98b0', '#6a7a98', '#566789', '#4b5e83', '#3c4d6f', '#2a3550', '#141a28',
];

export const theme = createTheme({
  primaryColor: 'signal',
  primaryShade: { light: 7, dark: 5 },
  colors: { signal, dark: ['#c9d1e0', '#a6b0c3', '#7d889e', '#5a6478', '#3a4256', '#2a3142', '#1d2331', '#151a25', '#10141d', '#0b0e15'], ink },
  fontFamily: 'Inter, "Segoe UI", system-ui, -apple-system, sans-serif',
  fontFamilyMonospace: '"JetBrains Mono", "SFMono-Regular", Consolas, monospace',
  defaultRadius: 'xs',
  fontSizes: { xs: '11px', sm: '12px', md: '13px', lg: '15px', xl: '18px' },
  spacing: { xs: '4px', sm: '8px', md: '12px', lg: '16px', xl: '24px' },
  headings: { fontWeight: '600' },
  focusRing: 'always',
  cursorType: 'pointer',
});
