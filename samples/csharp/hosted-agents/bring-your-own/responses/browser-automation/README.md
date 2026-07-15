# Browser Automation Agent (C#, BYO Responses)

This sample runs a C# Responses agent that uses a Microsoft Foundry toolbox and
an Azure Playwright workspace to navigate pages, inspect content, fill forms,
and run browser tasks in parallel sessions.

## How it works

The Responses handler sends each request to a model deployed in a Foundry
project. When the model selects a browser tool, the agent creates a remote
browser session through the `browser-automation-tools` toolbox, attaches
`playwright-cli` to that session, and executes the requested browser commands.
The default session is created lazily on the first browser command.

## Prerequisites

1. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later.
2. [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
   with the Foundry extension:

   ```bash
   azd ext install microsoft.foundry
   azd auth login
   az login
   ```

3. An existing Foundry project containing:
   - A deployed chat model.
   - A published toolbox connected to an Azure Playwright workspace. The
     manifest and code use `browser-automation-tools` by default.
4. `Azure AI User` access to the Foundry project and access to the configured
   toolbox and Playwright workspace.

## Run locally with `azd`

Create a dedicated environment for this sample. Replace the placeholders with
the subscription, location, existing Foundry project endpoint, and model
deployment you intend to use:

```bash
azd env new <environment-name> --subscription <subscription-id> --location <location>
azd env set FOUNDRY_PROJECT_ENDPOINT <project-endpoint> --environment <environment-name>
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME <model-deployment-name> --environment <environment-name>
azd env set TOOLBOX_NAME browser-automation-tools --environment <environment-name>
```

Start the local Responses server. Pass the environment explicitly so another
`azd` project's active environment is not used:

```bash
azd ai agent run --environment <environment-name> --port 8088 --no-client
```

Wait for `http://127.0.0.1:8088/readiness` to return HTTP 200.

In another terminal, exercise the browser capability through the local
`/responses` endpoint:

```bash
azd ai agent invoke --local --environment <environment-name> --port 8088 --protocol responses --new-session "/verbose Navigate to https://example.com using the browser, inspect the page, and report its exact page title and main heading."
```

A successful response includes browser tool activity and reports `Example
Domain` as both the page title and main heading. The `/verbose` prefix exposes
the tool activity for local diagnostics.

## Configuration

| Variable | Required | Description |
| --- | --- | --- |
| `FOUNDRY_PROJECT_ENDPOINT` | Yes | Foundry project endpoint, such as `https://<account>.services.ai.azure.com/api/projects/<project>`. |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Yes | Model deployment in the selected Foundry project. |
| `TOOLBOX_NAME` | No | Published browser toolbox name. Defaults to `browser-automation-tools`. |
| `BROWSER_TIMEOUT_SECONDS` | No | Browser command timeout. Defaults to 180 seconds. |

## Troubleshooting

- **The agent starts but the browser tool cannot connect:** Confirm the toolbox
  is published, its default version contains the browser automation tool, and
  its connection points to an accessible Azure Playwright workspace.
- **Authentication fails locally:** Run both `azd auth login` and `az login`,
  then verify your identity has access to the Foundry project.
- **`playwright-cli` is not found:** Install the CLI and ensure its executable
  directory is on `PATH`. On Windows, the sample resolves the npm JavaScript
  entry point and invokes it with Node.js so CDP URL parameters are preserved.

## Next steps

- [Deploy a hosted agent](https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent)
- [Hosted agents overview](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents)
