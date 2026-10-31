import { useEffect, useState } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, runStreamUrl } from './client';
import type { RunDetail } from './types';
import { isActive } from '../lib/format';

/** How often the list pages refresh. Runs change faster than agents or pipelines. */
const RUNS_REFRESH_MS = 2000;
const SLOW_REFRESH_MS = 5000;

export const usePipelines = () =>
  useQuery({ queryKey: ['pipelines'], queryFn: api.pipelines, refetchInterval: SLOW_REFRESH_MS });

export const useRuns = (query: { active?: boolean; pipelineId?: string; limit?: number }) =>
  useQuery({
    queryKey: ['runs', query],
    queryFn: () => api.runs(query),
    refetchInterval: RUNS_REFRESH_MS,
    placeholderData: keepPreviousData,
  });

export const useAgents = () =>
  useQuery({ queryKey: ['agents'], queryFn: api.agents, refetchInterval: SLOW_REFRESH_MS });

export function useTriggerRun() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (pipelineId: string) => api.triggerRun(pipelineId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['pipelines'] });
      void queryClient.invalidateQueries({ queryKey: ['runs'] });
    },
  });
}

export interface RunStream {
  detail: RunDetail | null;
  /** The server has no such run. */
  notFound: boolean;
  /** The stream dropped before the run finished; the browser is retrying. */
  reconnecting: boolean;
}

/**
 * Follows one run live over server-sent events. The server sends the full run detail on every
 * change and ends the stream once the run has finished - which the browser would otherwise treat
 * as a dropped connection and reconnect to forever, so it is closed here as soon as a finished
 * run arrives.
 */
export function useRunStream(runId: string): RunStream {
  const [detail, setDetail] = useState<RunDetail | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [reconnecting, setReconnecting] = useState(false);

  useEffect(() => {
    setDetail(null);
    setNotFound(false);
    setReconnecting(false);

    const source = new EventSource(runStreamUrl(runId));
    let received = false;

    source.addEventListener('run', (event) => {
      received = true;
      setReconnecting(false);
      const next = JSON.parse((event as MessageEvent<string>).data) as RunDetail;
      setDetail(next);
      if (!isActive(next.run.status)) source.close();
    });

    source.onerror = () => {
      if (source.readyState === EventSource.CLOSED) {
        // The server refused the stream outright (an unknown run is a 404) - nothing to retry.
        if (!received) setNotFound(true);
      } else {
        setReconnecting(true);
      }
    };

    return () => source.close();
  }, [runId]);

  return { detail, notFound, reconnecting };
}

/** `Date.now()`, re-read every `intervalMs` - for durations and uptimes that tick between fetches. */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [intervalMs]);
  return now;
}
