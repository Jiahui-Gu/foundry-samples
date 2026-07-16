# Browser Automation Agent (C#, BYO Responses)

This Bring Your Own hosted agent uses the Responses protocol to automate
browsers through a Microsoft Foundry Toolbox backed by an Azure Playwright
workspace. It supports browser navigation, page inspection, form filling,
scraping, and multiple concurrent browser sessions.

## How it works

The Responses handler sends the user's request and browser tool definitions to
the configured Foundry model. When the model calls `run_browser`, the agent
lazily creates a remote Chromium session through Toolbox MCP, attaches
`playwright-cli` to it, and returns the browser observations to the model.

Available tools include:

- `run_browser` to navigate and interact with a page
- `create_session`, `list_sessions`, and `end_session` to manage browsers
- `run_parallel` to operate multiple sessions concurrently
- `load_skill` to load the bundled form-filling or web-scraping instructions

See `src/browser-automation-csharp-byo/Program.cs` for the Responses handler and
tool loop.

### Configuration

| Variable | Required | Description |
| --- | --- | --- |
| `FOUNDRY_PROJECT_ENDPOINT` | Yes | Project endpoint, such as `https://<account>.services.ai.azure.com/api/projects/<project>` |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Yes | Name of a model deployment in that Foundry project |
| `TOOLBOX_NAME` | No | Toolbox containing `browser_automation_preview`; defaults to `browser-automation-tools` |
| `BROWSER_TIMEOUT_SECONDS` | No | Timeout for each browser command; defaults to `180` |

## Prerequisites

1. A Microsoft Foundry project with:
   - a deployed tool-calling model, such as the `gpt-5.4-mini` deployment
     declared in `azure.yaml`
   - a toolbox containing the `browser_automation_preview` tool
   - a project connection from that tool to an Azure Playwright workspace
2. The **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**.
3. Azure CLI authentication with access to the project (`az login`). The local
   identity needs the **Azure AI User** role on the Foundry project.

## Option 1: Azure Developer CLI (`azd`)

### Prerequisites

1. Install the [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd).
2. Install the Microsoft Foundry extension:

   ```bash
   azd ext install microsoft.foundry
   ```

3. Authenticate:

   ```bash
   az login
   azd auth login
   ```

### Configure an existing Foundry project

From this sample directory, create a dedicated azd environment and record the
existing dependency endpoints. Replace the placeholders with resources from
the same Foundry project:

```bash
azd env new <environment-name> --subscription <subscription-id> --location <location>
azd env set FOUNDRY_PROJECT_ENDPOINT "https://<account>.services.ai.azure.com/api/projects/<project>"
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME "gpt-5.4-mini"
azd env set TOOLBOX_NAME "browser-automation-tools"
```

The model deployment and toolbox must both exist in the project identified by
`FOUNDRY_PROJECT_ENDPOINT`.

### Run the agent locally

```bash
azd ai agent run --no-client
```

The agent starts on `http://localhost:8088`. Its readiness endpoint is
`http://localhost:8088/readiness`.

### Invoke the local agent

In a second terminal, from this sample directory:

```bash
azd ai agent invoke --local \
  "Use browser automation to visit https://example.com and report the page title."
```

This sends a Responses request to the local `/responses` endpoint and exercises
the `run_browser` tool. A successful result reports the title `Example Domain`.

### Provision and deploy new resources

To create the project, model, Playwright connection, toolbox, and hosted agent
declared by `azure.yaml`, initialize the manifest in a separate empty directory.
Do not initialize in this sample directory because it already contains
`azure.yaml`.

```bash
mkdir browser-automation-deployment
cd browser-automation-deployment
azd ai agent init -m <path-or-url-to-this-sample>/azure.yaml
azd env set PLAYWRIGHT_SERVICE_URL "<playwright-workspace-websocket-url>"
azd env set PLAYWRIGHT_SERVICE_RESOURCE_ID "<playwright-workspace-resource-id>"
azd env set PLAYWRIGHT_SERVICE_ACCESS_TOKEN "<playwright-workspace-access-token>"
azd up
```

Invoke the deployed agent with:

```bash
azd ai agent invoke \
  "Use browser automation to visit https://example.com and report the page title."
```

## Option 2: VS Code (Foundry Toolkit)

### Prerequisites

1. Install VS Code with the
   [Foundry Toolkit](https://marketplace.visualstudio.com/items?itemName=ms-windows-ai-studio.windows-ai-studio).
2. Install the [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit).
3. Configure the environment variables in the table above and run `az login`.

### Run and inspect

Start the project from `src/browser-automation-csharp-byo`:

```bash
dotnet run
```

Then run **Foundry Toolkit: Open Agent Inspector** from the Command Palette and
send the representative browser request shown above.

## Troubleshooting

- **`DeploymentNotFound`:** Verify that
  `AZURE_AI_MODEL_DEPLOYMENT_NAME` names a deployment in the same project as
  `FOUNDRY_PROJECT_ENDPOINT`.
- **Toolbox or MCP errors:** Verify that `TOOLBOX_NAME` exists in that project
  and contains `browser_automation_preview` backed by a valid Playwright
  connection.
- **Authentication errors:** Run both `az login` and `azd auth login`, then
  verify that your identity has the Azure AI User role on the project.

## Next steps

- [Hosted agents overview](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents)
- [Azure Playwright Workspaces](https://learn.microsoft.com/azure/playwright-testing/)
