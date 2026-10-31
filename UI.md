# WebUI 

Create a folder called 'web' on the root of the project.
In the folder create a reactJS application using bun, vite and mantine as the UI library.
The UI will connect to the EtlPipelines server. I don't think we can use the GRPC api for this. If not then create REST endpoints using minimal APIs.
User will be able to see available jobs and manually trigger a run.
User should be able to see jobs that are currently running and their progress. Opening a running job, the UI should show a visual representation of the jobs steps and live stats coming from the agent on the progress of each job.
User should also be able to see available agents and see information about the agents like OS, uptime, IP, etc, along with list of running jobs.