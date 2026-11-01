import { Alert, Button, Group, Modal, MultiSelect, Stack, TagsInput, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle } from '@tabler/icons-react';
import { useEffect, useRef, useState } from 'react';
import { useAgents, useSetAssignment } from '../api/hooks';
import type { Pipeline } from '../api/types';
import { eligibleAgents, isAnyAgent, knownTags } from '../lib/assignment';
import { pipelineLabel } from '../lib/pipelineLabels';

/**
 * Chooses which agents may run a pipeline: the ones with any of some tags, and/or agents named
 * explicitly. Nothing chosen means any agent. Shows who that comes to as it is edited.
 */
export function AssignAgentsModal({ pipeline, opened, onClose }: { pipeline: Pipeline; opened: boolean; onClose: () => void }) {
  const save = useSetAssignment();
  const { data: agents = [] } = useAgents();
  const [tags, setTags] = useState(pipeline.assignment.tags);
  const [agentIds, setAgentIds] = useState(pipeline.assignment.agentIds);

  // Start from where it is assigned now each time the dialog is opened - and only then: the pipeline is refreshed
  // whenever a run starts or ends, which must not throw away what is being chosen.
  const wasOpened = useRef(false);
  useEffect(() => {
    if (opened && !wasOpened.current) {
      setTags(pipeline.assignment.tags);
      setAgentIds(pipeline.assignment.agentIds);
    }

    wasOpened.current = opened;
  }, [opened, pipeline.assignment]);

  const assignment = { tags: [...new Set(tags.map((tag) => tag.trim().toLowerCase()).filter(Boolean))], agentIds };
  const eligible = eligibleAgents(assignment, agents);
  const online = eligible.filter((agent) => agent.status === 'Online');
  const unchanged =
    assignment.tags.join('\n') === pipeline.assignment.tags.join('\n') &&
    [...assignment.agentIds].sort().join('\n') === [...pipeline.assignment.agentIds].sort().join('\n');

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    save.mutate(
      { pipelineId: pipeline.id, assignment },
      {
        onSuccess: () => {
          notifications.show({
            color: 'green',
            title: 'Saved',
            message: isAnyAgent(assignment)
              ? `${pipelineLabel(pipeline)} may run on any agent.`
              : `${pipelineLabel(pipeline)} runs on ${eligible.length} agent${eligible.length === 1 ? '' : 's'}.`,
          });
          onClose();
        },
        onError: (error) => notifications.show({ color: 'red', title: 'Could not save', message: error.message }),
      },
    );
  };

  return (
    <Modal opened={opened} onClose={onClose} title={`Where ${pipelineLabel(pipeline)} runs`} centered size="lg">
      <form onSubmit={submit}>
        <Stack>
          <Text size="sm" c="dimmed">
            A run is sent to an agent that has <b>any</b> of the tags below, or that is named below. Leave both empty to let any agent run it.
          </Text>
          <TagsInput
            label="Agents with these tags"
            placeholder="gpu, eu-west, ..."
            value={tags}
            onChange={setTags}
            data={knownTags(agents)}
            maxTags={20}
            maxLength={50}
            splitChars={[',', ' ']}
            clearable
          />
          <MultiSelect
            label="These agents"
            placeholder={agentIds.length ? undefined : 'Pick agents by name'}
            value={agentIds}
            onChange={setAgentIds}
            data={agents.map((agent) => ({
              value: agent.id,
              label: agent.status === 'Online' ? agent.machineName : `${agent.machineName} (offline)`,
            }))}
            searchable
            clearable
            nothingFoundMessage="No such agent"
          />

          {isAnyAgent(assignment) ? (
            <Text size="sm">Any agent may run it ({agents.filter((agent) => agent.status === 'Online').length} online now).</Text>
          ) : online.length > 0 ? (
            <Text size="sm">
              It can run on {eligible.length} agent{eligible.length === 1 ? '' : 's'}, {online.length} online now:{' '}
              <b>{eligible.map((agent) => agent.machineName).join(', ')}</b>.
            </Text>
          ) : (
            <Alert color="orange" icon={<IconAlertTriangle size={18} />} title="No agent online can run it">
              {eligible.length === 0
                ? 'No agent has these tags or is named, so a run would be refused until one does.'
                : `Only offline agents match (${eligible.map((agent) => agent.machineName).join(', ')}), so a run would be refused until one is back.`}
            </Alert>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending} disabled={unchanged}>
              Save
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
