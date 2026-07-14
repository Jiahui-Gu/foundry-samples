# Browser Automation Agent (C#, BYO Responses)

This sample implements a Foundry-hosted Responses agent that uses a Foundry
Toolbox and Playwright workspace to browse pages, scrape content, and fill
forms.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
  with the `azure.ai.agents` extension
- An Azure login (`az login`) with access to an existing Foundry project
- A model deployment named `gpt-5.4-mini`, or another Responses-capable model
- A Foundry toolbox containing the `browser_automation_preview` tool and a
  Playwright workspace connection

## Run locally

Create a dedicated local environment from this sample directory:

```bash
azd env new browser-automation-local --subscription <subscription-id> --location <azure-region>
azd env set -e browser-automation-local \
  FOUNDRY_PROJECT_ENDPOINT=https://<account>.services.ai.azure.com/api/projects/<project> \
  AZURE_AI_MODEL_DEPLOYMENT_NAME=gpt-5.4-mini \
  TOOLBOX_NAME=browser-automation-tools
```

Start the agent:

```bash
azd ai agent run -e browser-automation-local
```

Wait for `Now listening on: http://[::]:8088`, then invoke it from another
terminal:

```bash
azd ai agent invoke -e browser-automation-local --local "hello, are you up"
```

To use another port, pass the same `--port <port>` value to both `run` and
`invoke`.

For a browser test, ask the agent to open a public page and report its title.
Browser requests require the configured toolbox and Playwright connection;
simple conversational requests only require the model deployment.

## Configuration

| Variable | Description |
| --- | --- |
| `FOUNDRY_PROJECT_ENDPOINT` | Existing Foundry project endpoint |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Responses-capable model deployment |
| `TOOLBOX_NAME` | Foundry toolbox containing the browser automation tool |
| `BROWSER_TIMEOUT_SECONDS` | Optional browser command timeout; defaults to 180 seconds |

Copy `src/browser-automation-csharp-byo/.env.example` when running with
`dotnet run` instead of `azd ai agent run`.
