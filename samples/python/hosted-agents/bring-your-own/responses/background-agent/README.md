**IMPORTANT!** All samples and other resources made available in this GitHub repository ("samples") are designed to assist in accelerating development of agents, solutions, and agent workflows for various scenarios. Review all provided resources and carefully test output behavior in the context of your use case. AI responses may be inaccurate and AI actions should be monitored with human oversight.

# Background Agent — Responses Protocol (Long-Running)

This sample demonstrates a long-running agent built with [azure-ai-agentserver-responses](https://pypi.org/project/azure-ai-agentserver-responses/) that uses the background execution mode for asynchronous processing. It calls Azure OpenAI to generate a multi-section research analysis, streaming LLM tokens as they arrive via the Responses API event lifecycle.

## How It Works

The agent receives a request via `POST /responses` with `"background": true`. The server returns immediately while the handler calls Azure OpenAI in the background, streaming response tokens as `text.delta` events. The caller polls `GET /responses/{id}` until the response reaches a terminal status (`completed`, `failed`, or `incomplete`). In-flight requests can be cancelled via `POST /responses/{id}/cancel`.

## Option 1: Azure Developer CLI (`azd`)

### Prerequisites

- Python 3.12+
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) with the Foundry extension:

  ```bash
  azd ext install microsoft.foundry
  ```

- Azure CLI installed and authenticated (`az login`)
- Foundry project with a deployed model that your signed-in identity can access

### Configure the local environment

Create an `azd` environment for the project, then set the endpoint and model
deployment used by the local process:

```bash
azd env new <environment-name> \
  --subscription <subscription-id> \
  --location <project-location>
azd env set FOUNDRY_PROJECT_ENDPOINT \
  "https://<account>.services.ai.azure.com/api/projects/<project>" \
  --environment <environment-name>
azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME \
  "<model-deployment-name>" \
  --environment <environment-name>
```

### Run the agent locally

```bash
azd ai agent run --environment <environment-name>
```

The agent starts on `http://localhost:8088/`.

On a headless system, add `--no-client`. If port 8088 is unavailable, add
`--port <local-port>` and pass the same port when invoking the agent. Replace
8088 in the curl examples as well.

### Invoke the local agent

```bash
azd ai agent invoke --environment <environment-name> \
  --local "Analyze the impact of AI on healthcare"
```

For an alternate port:

```bash
azd ai agent invoke --environment <environment-name> \
  --local --port <local-port> \
  "Analyze the impact of AI on healthcare"
```

Or test background and synchronous modes directly with curl:

```bash
# Background mode — submit a research analysis
curl -X POST http://localhost:8088/responses \
  -H "Content-Type: application/json" \
  -d '{"model": "research", "input": "Analyze the impact of AI on healthcare", "background": true, "store": true}'

# Poll for result (use the id from the response)
curl http://localhost:8088/responses/<response_id>

# Cancel an in-flight request
curl -X POST http://localhost:8088/responses/<response_id>/cancel

# Default (synchronous) mode
curl -X POST http://localhost:8088/responses \
  -H "Content-Type: application/json" \
  -d '{"model": "research", "input": "Analyze the impact of AI on healthcare"}'
```

### Deploy to Foundry

Once tested locally, deploy to Microsoft Foundry:

```bash
azd provision
azd deploy
```

### Invoke the deployed agent

```bash
azd ai agent invoke "Analyze the impact of AI on healthcare"
```

To stream logs from the running agent:

```bash
azd ai agent monitor
```

For the full deployment guide, see [Azure AI Foundry hosted agents](https://aka.ms/azdaiagent/docs).

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

## Troubleshooting

### Images built on Apple Silicon or other ARM64 machines do not work on our service

**Deploy with `azd deploy`**, which uses ACR remote build and always produces images with the correct architecture.

If you choose to **build locally**, and your machine is **not `linux/amd64`** (for example, an Apple Silicon Mac), the image will **not be compatible with our service**, causing runtime failures.

**Fix for local builds:**

```bash
docker build --platform=linux/amd64 -t image .
```

This forces the image to be built for the required `amd64` architecture.
