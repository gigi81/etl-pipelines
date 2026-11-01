import { Badge, Button, Group, Modal, Stack, TagsInput, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useEffect, useRef, useState } from 'react';
import { useAgents, useSetAgentTags } from '../api/hooks';
import type { Agent } from '../api/types';
import { knownTags } from '../lib/assignment';

/**
 * Gives an agent tags - labels like `gpu` or `eu-west` that pipelines can be assigned to. The tags an
 * agent reports about itself (its `Agent:Tags` setting) are shown but not editable here: they are set
 * where the agent is configured, and come back each time it starts.
 */
export function AgentTagsModal({ agent, opened, onClose }: { agent: Agent; opened: boolean; onClose: () => void }) {
  const save = useSetAgentTags();
  const { data: agents = [] } = useAgents();
  const [tags, setTags] = useState(agent.assignedTags);

  // Start from the agent's tags each time the dialog is opened - and only then: the agent is refreshed all the
  // time (every heartbeat and run), which must not throw away what is being typed.
  const wasOpened = useRef(false);
  useEffect(() => {
    if (opened && !wasOpened.current) setTags(agent.assignedTags);
    wasOpened.current = opened;
  }, [opened, agent.assignedTags]);

  const reported = agent.tags.filter((tag) => !agent.assignedTags.some((assigned) => assigned.toLowerCase() === tag.toLowerCase()));
  const normalized = [...new Set(tags.map((tag) => tag.trim().toLowerCase()).filter(Boolean))];
  const unchanged = normalized.join('\n') === agent.assignedTags.join('\n');

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    save.mutate(
      { agentId: agent.id, tags: normalized },
      {
        onSuccess: () => {
          notifications.show({ color: 'green', title: 'Tags saved', message: `${agent.machineName}: ${normalized.join(', ') || 'no tags'}.` });
          onClose();
        },
        onError: (error) => notifications.show({ color: 'red', title: 'Could not save the tags', message: error.message }),
      },
    );
  };

  return (
    <Modal opened={opened} onClose={onClose} title={`Tags of ${agent.machineName}`} centered>
      <form onSubmit={submit}>
        <Stack>
          <TagsInput
            label="Tags"
            description="Type a tag and press Enter. Letters, digits and . _ : / - ; upper case is made lower case."
            placeholder="gpu, eu-west, ..."
            value={tags}
            onChange={setTags}
            data={knownTags(agents)}
            maxTags={20}
            maxLength={50}
            splitChars={[',', ' ']}
            clearable
            data-autofocus
          />
          {reported.length > 0 && (
            <div>
              <Text size="sm" fw={500}>
                Set by the agent itself
              </Text>
              <Group gap={4} mt={4}>
                {reported.map((tag) => (
                  <Badge key={tag} variant="outline" color="gray" tt="none">
                    {tag}
                  </Badge>
                ))}
              </Group>
              <Text size="xs" c="dimmed" mt={4}>
                These come from the agent&apos;s <code>Agent:Tags</code> setting; change them there.
              </Text>
            </div>
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
