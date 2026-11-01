import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { applyAgentEvent, applyAgentRemoved, applyRunEvent } from '../lib/liveCache';
import type { Agent, RunSummary } from './types';

/** How long to wait before reconnecting a stream the browser gave up on. */
const RECONNECT_MS = 3000;

/**
 * Keeps the lists on screen current from the server's change feed (`/api/events`): the server pushes
 * each change as it happens, so nothing is polled. The feed carries changes only - the lists are
 * fetched normally - so every time it (re)connects, everything is refetched once to cover whatever
 * was missed in between.
 *
 * Returns whether the feed is connected.
 */
export function useLiveUpdates(): boolean {
  const queryClient = useQueryClient();
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    let source: EventSource | undefined;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let disposed = false;

    const connect = () => {
      source = new EventSource('/api/events');

      // The first thing the server sends, once it is listening.
      source.addEventListener('ready', () => {
        setConnected(true);
        void queryClient.invalidateQueries();
      });
      source.addEventListener('run', (event) =>
        applyRunEvent(queryClient, JSON.parse((event as MessageEvent<string>).data) as RunSummary),
      );
      source.addEventListener('agent', (event) =>
        applyAgentEvent(queryClient, JSON.parse((event as MessageEvent<string>).data) as Agent),
      );
      source.addEventListener('agent-removed', (event) => {
        applyAgentRemoved(queryClient, (JSON.parse((event as MessageEvent<string>).data) as { id: string }).id);
        // A pipeline assigned to the agent no longer lists it.
        void queryClient.invalidateQueries({ queryKey: ['pipelines'] });
      });

      // A package was installed, updated or removed, a pipeline was renamed or had its history cleared (by
      // anyone): the pipelines, the packages and the runs lists change.
      source.addEventListener('pipelines', () => {
        void queryClient.invalidateQueries({ queryKey: ['pipelines'] });
        void queryClient.invalidateQueries({ queryKey: ['packages'] });
        void queryClient.invalidateQueries({ queryKey: ['runs'] });
      });

      source.onerror = () => {
        setConnected(false);
        // The browser retries by itself after a dropped connection, but gives up for good on an
        // HTTP error (a proxy answering 502 while the server restarts) - so start over by hand.
        if (source?.readyState === EventSource.CLOSED && !disposed) {
          source.close();
          retry = setTimeout(connect, RECONNECT_MS);
        }
      };
    };

    connect();
    return () => {
      disposed = true;
      clearTimeout(retry);
      source?.close();
    };
  }, [queryClient]);

  return connected;
}
