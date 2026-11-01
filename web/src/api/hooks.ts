import { useEffect, useMemo, useState } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, runStreamUrl } from './client';
import type { RunDetail } from './types';
import { isActive } from '../lib/format';
import { buildPipelineLabeller } from '../lib/pipelineLabels';

// The lists below are fetched once and then kept current by `useLiveUpdates` (the server pushes
// every change), so none of them polls.
export const usePipelines = () =>
  useQuery({ queryKey: ['pipelines'], queryFn: api.pipelines });

export const useRuns = (query: { active?: boolean; pipelineId?: string; limit?: number }) =>
  useQuery({
    queryKey: ['runs', query],
    queryFn: () => api.runs(query),
    placeholderData: keepPreviousData,
  });

/** `(packageId, pipelineName) => the name to show`, so a run is labelled like its pipeline is. */
export function usePipelineLabeller() {
  const { data } = usePipelines();
  return useMemo(() => buildPipelineLabeller(data), [data]);
}

export const useAgents = () =>
  useQuery({ queryKey: ['agents'], queryFn: api.agents });

/**
 * The feed's packages and the installed ones. Unlike the lists above nothing pushes the feed's
 * contents, so this is fetched fresh each time the page is opened (and after every change made here).
 */
export const usePackages = (search: string) =>
  useQuery({
    queryKey: ['packages', search],
    queryFn: () => api.packages(search || undefined),
    staleTime: 0,
    placeholderData: keepPreviousData,
  });

export const useConfigurationKeys = () => useQuery({ queryKey: ['configuration'], queryFn: api.configurationKeys });

/** What changes when packages are installed, updated, removed or uploaded. */
function usePackagesChanged() {
  const queryClient = useQueryClient();
  return () => {
    void queryClient.invalidateQueries({ queryKey: ['packages'] });
    void queryClient.invalidateQueries({ queryKey: ['pipelines'] });
  };
}

export function useInstallPackage() {
  const changed = usePackagesChanged();
  return useMutation({
    mutationFn: ({ id, version }: { id: string; version?: string }) => api.installPackage(id, version),
    onSuccess: changed,
  });
}

export function useUpdatePackage() {
  const changed = usePackagesChanged();
  return useMutation({ mutationFn: (id: string) => api.updatePackage(id), onSuccess: changed });
}

export function useUninstallPackage() {
  const changed = usePackagesChanged();
  return useMutation({ mutationFn: (id: string) => api.uninstallPackage(id), onSuccess: changed });
}

export function useUploadPackage() {
  const changed = usePackagesChanged();
  return useMutation({ mutationFn: (file: File) => api.uploadPackage(file), onSuccess: changed });
}

export function useSetConfiguration() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => api.setConfiguration(key, value),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['configuration'] }),
  });
}

export function useDeleteConfiguration() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => api.deleteConfiguration(key),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['configuration'] }),
  });
}

export function useRemoveAgent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.removeAgent(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['agents'] }),
  });
}

export function useSetDisplayName() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ pipelineId, displayName }: { pipelineId: string; displayName: string }) =>
      api.setDisplayName(pipelineId, displayName),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['pipelines'] }),
  });
}

export function useClearHistory() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (pipelineId: string) => api.clearHistory(pipelineId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['pipelines'] });
      void queryClient.invalidateQueries({ queryKey: ['runs'] });
    },
  });
}

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
