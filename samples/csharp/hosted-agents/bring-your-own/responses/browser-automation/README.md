# Browser Automation Agent (C#, BYO Responses)

This sample implements a Microsoft Foundry hosted agent that uses the
Responses protocol, Foundry Toolbox, and an Azure Playwright workspace to
navigate pages, scrape data, and fill forms.

## How it works

1. The agent sends the user's request to a model deployed in a Foundry project.
2. The model calls the local browser tools when the request requires browser
   interaction.
3. The agent asks the `browser-automation-tools` Foundry Toolbox for a remote
   browser session.
4. The local Playwright CLI attaches to that session and executes browser
   commands. CDP URLs and access tokens are redacted from logs and model output.

The C# process exposes the Responses endpoint on port 8088 by default. Browser
sessions are created lazily and can be reused across requests.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
  with the Microsoft Foundry extension
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
- Node.js 22 or later and the Playwright CLI
- An existing Foundry project containing:
  - a deployed chat model
  - a published Toolbox with the `browser_automation_preview` tool
  - a `PlaywrightWorkspace` connection used by that tool

Install the local browser command dependency:

```bash
npm install -g @playwright/cli@latest
playwright-cli install --skills
```

Authenticate both local credential providers:

```bash
az login
azd auth login
```

## Configuration

The local process uses these environment values:

| Variable | Required | Purpose |
| --- | --- | --- |
| `FOUNDRY_PROJECT_ENDPOINT` | Yes | Project endpoint, such as `https://<account>.services.ai.azure.com/api/projects/<project>`. |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Yes | Existing model deployment used by the agent. |
| `TOOLBOX_NAME` | No | Existing browser Toolbox name. Defaults to `browser-automation-tools`. |
| `BROWSER_TIMEOUT_SECONDS` | No | Timeout for each Playwright CLI command. Defaults to 180 seconds. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No | Enables local Application Insights telemetry. |

The Playwright service URL, resource ID, and access token in `.env.example`
are provisioning inputs. They are not needed for a local run when the selected
project already contains the connection and Toolbox.

## Run locally with `azd`

From this sample directory, create a dedicated environment and set the runtime
values. Replace the placeholders with an existing project and deployment:

```bash
azd env new browser-automation-local
azd env set -e browser-automation-local \
  FOUNDRY_PROJECT_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>" \
  AZURE_AI_MODEL_DEPLOYMENT_NAME="<deployment-name>" \
  TOOLBOX_NAME="<toolbox-name>"
```

Start the local agent:

```bash
azd ai agent run -e browser-automation-local
```

Wait for `Now listening on: http://[::]:8088`, then invoke a representative
browser workflow from another terminal:

```bash
azd ai agent invoke --local -e browser-automation-local --new-session \
  "Open https://example.com with the browser tools and report its page title."
```

Use the same `--port` value on both commands to select a port other than 8088.

## Run manually

Set the runtime variables in your shell, restore the project, and start it:

```bash
dotnet restore src/browser-automation-csharp-byo/browser-automation.csproj
dotnet run --project src/browser-automation-csharp-byo/browser-automation.csproj
```

The process uses `DefaultAzureCredential`, so keep the Azure CLI or Azure
Developer CLI login available. You can invoke it with `azd ai agent invoke
--local` or open **Foundry Toolkit: Open Agent Inspector** in VS Code.

## Deploy

The unified [`azure.yaml`](azure.yaml) describes the model, Playwright
connection, Toolbox, and hosted agent. Follow
[Deploy a hosted agent](https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent)
when you are ready to provision or reuse those resources and deploy the
container.

## Troubleshooting

- **`playwright-cli was not found on PATH`**: install the Playwright CLI from
  the prerequisites, open a new terminal, and confirm `playwright-cli --help`
  succeeds.
- **Toolbox or browser session errors**: confirm `TOOLBOX_NAME` identifies a
  published Toolbox with a `browser_automation_preview` tool and a working
  Playwright workspace connection in the same Foundry project.
- **Authentication errors**: rerun `az login` and `azd auth login`, and confirm
  the signed-in identity can use the model, Toolbox, and Playwright workspace.
- **Model not found**: set `AZURE_AI_MODEL_DEPLOYMENT_NAME` to a deployment that
  exists in the selected project.

## Next steps

Customize the system prompt and tool schemas in
[`Constants.cs`](src/browser-automation-csharp-byo/utils/Constants.cs), or add
guided browser workflows under
[`skills/`](src/browser-automation-csharp-byo/skills/).
