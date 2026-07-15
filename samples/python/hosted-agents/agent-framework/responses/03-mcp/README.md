# Agent with Remote MCP Tools (Responses Protocol)

An [Agent Framework](https://github.com/microsoft/agent-framework) agent that connects to a **remote MCP server** (GitHub) for tool discovery, hosted on Microsoft Foundry using the **Responses protocol**. Instead of defining tools locally, the agent discovers and invokes tools at runtime from an MCP-compatible endpoint — in this case, the GitHub Copilot MCP server. This enables dynamic tool integration without redeployment.

## How it works

The agent uses `FoundryChatClient` from the Agent Framework and is served via `ResponsesHostServer`. It registers a remote MCP tool pointing at `https://api.githubcopilot.com/mcp/`, authenticating with a GitHub PAT. When the model decides to call a tool, the framework forwards the call to the MCP server and returns the result to the model for the final reply. See [main.py](src/agent-framework-agent-with-remote-mcp-tools-responses/main.py) for the implementation.

## Option 1: Azure Developer CLI (`azd`)

### Prerequisites

1. **Azure Developer CLI (`azd`)** — [Install azd](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd)
2. Install the AI agent extension:
   ```bash
   azd ext install microsoft.foundry
   ```
3. Authenticate:
   ```bash
   azd auth login
   ```
4. **GitHub Personal Access Token (PAT)** — required for authenticating with the GitHub Copilot MCP server. [Create a PAT](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens).

### Run this checkout directly

Use this path when you cloned the repository and want to test the sample without
generating another project. It requires an existing Foundry project with a
deployed model, Python 3.12 or later, and an authenticated Azure CLI session.

From this sample directory, create the virtual environment beside the checkout
rather than under the deeply nested service directory. This avoids Windows path
length failures while keeping dependencies isolated:

```powershell
$service = Join-Path $PWD "src\agent-framework-agent-with-remote-mcp-tools-responses"
$venv = Join-Path (Split-Path -Parent (git rev-parse --show-toplevel)) "mcp-agent-venv"

python -m venv $venv
& "$venv\Scripts\python.exe" -m pip install uv
& "$venv\Scripts\uv.exe" pip install --python "$venv\Scripts\python.exe" --prerelease=allow -r "$service\requirements.txt"

Copy-Item "$service\.env.example" "$service\.env"
```

Set `FOUNDRY_PROJECT_ENDPOINT`, `AZURE_AI_MODEL_DEPLOYMENT_NAME`, and
`GITHUB_PAT` in the new `.env` file. Then start the checked-out source on an
explicit unused port:

```powershell
$port = 8088
$env:PORT = "$port"
Set-Location $service
& "$venv\Scripts\python.exe" main.py
```

In another PowerShell terminal, invoke the declared Responses endpoint on the
same port:

```powershell
$port = 8088
$body = '{"input":"List all the repositories I own on GitHub."}'
(Invoke-WebRequest -Uri "http://localhost:$port/responses" -Method POST -ContentType "application/json" -Body $body).Content
```

### Initialize the agent project

No cloning required. Create a new folder and initialize from the manifest:

```bash
mkdir my-mcp-agent && cd my-mcp-agent

azd ai agent init -m https://github.com/microsoft-foundry/foundry-samples/blob/main/samples/python/hosted-agents/agent-framework/responses/03-mcp/azure.yaml
```

Follow the prompts to configure your Foundry project and model deployment. If you don't have an existing Foundry project, `azd ai agent init` will guide you through creating one.

### Provision Azure resources (if needed)

If you don't already have a Foundry project and model deployment:

```bash
azd provision
```

### Set the GitHub PAT

Add your GitHub PAT to the `.env` file:

```
GITHUB_PAT="ghp_your_token_here"
```

### Run the agent locally

```bash
azd ai agent run
```

The agent host will start on `http://localhost:8088`.

### Invoke the local agent

In a separate terminal, from the project directory:

```bash
azd ai agent invoke --local "List all the repositories I own on GitHub."
```

### Deploy to Foundry

Once tested locally, deploy to Microsoft Foundry:

```bash
azd deploy
```

For the full deployment guide, see [Deploy a hosted agent](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/deploy-hosted-agent).

### Invoke the deployed agent

```bash
azd ai agent invoke "List all the repositories I own on GitHub."
```

## Option 2: VS Code (Foundry Toolkit)

### Prerequisites

1. **VS Code** with the **[Foundry Toolkit](https://marketplace.visualstudio.com/items?itemName=ms-windows-ai-studio.windows-ai-studio)** extension installed.
2. For debugging Python in VS Code, install the **[Python](https://marketplace.visualstudio.com/items?itemName=ms-python.python)** extension pack.

### Set up the Python virtual environment

- Open the Command Palette (`Ctrl+Shift+P`) and run **Python: Create Environment...** to create a virtual environment in the workspace (or **Python: Select Interpreter** to use an existing one).
- Install dependencies in the virtual environment:

  ```bash
  # use uv to accelerate
  pip install uv
  uv pip install -r requirements.txt

  # or pure pip
  pip install -r requirements.txt
  ```

### Run and debug the agent

Press **F5** to start the agent. The agent starts and the **Agent Inspector** opens automatically. Chat with the agent in the Inspector.

### Or run manually, then open the Inspector

1. Set the required environment variables and sign in to Azure with the Azure CLI (`az login`).
2. Start the agent: `python main.py` (listens on `http://localhost:8088`).
3. Command Palette (`Ctrl+Shift+P`) → **Foundry Toolkit: Open Agent Inspector**, then send a message to test.

### Deploy to Foundry

1. Open the Command Palette (`Ctrl+Shift+P`) and run **Foundry Toolkit: Deploy Hosted Agent**. The extension opens a **Deploy Hosted Agent** wizard and reads `agent.yaml` to auto-populate settings.
2. If prompted, complete **Foundry Project Setup** to select subscription and project.
3. On the **Basics** tab, choose deployment method (**Code** or **Container**) and confirm the agent name.
4. On **Review + Deploy**, confirm runtime details, pick **CPU and Memory** size, and click **Deploy**.
5. After deployment, invoke the agent in the Agent Playground and stream live logs from the **Logs** tab.

## Next steps

- [Quickstart: Create a hosted agent](https://learn.microsoft.com/en-us/azure/foundry/agents/quickstarts/quickstart-hosted-agent) — end-to-end walkthrough using `azd`
- [Tool catalog](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/tool-catalog) — browse available tools to extend your agent (Bing Search, Azure AI Search, file search, code interpreter, and more)
- [Manage hosted agents](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/manage-hosted-agent) — monitor and manage deployed agents
- [Basic agent](../01-basic/) — minimal agent with no tools
- [Add local tools](../02-tools/) — sample with locally-defined Python tool functions
- [Use Foundry Toolbox](../04-foundry-toolbox/) — sample with Azure Foundry Toolbox integration
