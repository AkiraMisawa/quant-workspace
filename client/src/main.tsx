import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import '@mantine/core/styles.css';
import '@mantine/spotlight/styles.css';
import '../../vendor/ex-grid/ex-grid.min.css';
import './theme.css';
import { App } from './App';
import { httpApi } from './platform/api';
import { theme } from './theme';

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } } });

// The workbench opens on a valid context so the first command can run at once; the user rebinds it.
const today = new Date().toISOString().slice(0, 10);

const root = document.getElementById('root');
if (root === null) throw new Error('index.html has no #root element.');
createRoot(root).render(
  <StrictMode>
    <MantineProvider theme={theme} defaultColorScheme="dark">
      <QueryClientProvider client={queryClient}>
        <App api={httpApi} initialContext={{ region: 'North', asOf: today }} />
      </QueryClientProvider>
    </MantineProvider>
  </StrictMode>,
);
