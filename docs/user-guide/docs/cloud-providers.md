# Connecting a cloud or remote model

The app works without any outside service. If you want to, you can also chat with models that run somewhere else:
a hosted API, a model server on another machine, or a company AI gateway. This page shows how to connect one, what
the app lets such a model do, and what leaves your computer when you use it.

There are two places to do it, both under **Settings** in the navigation:

| You want to use | Go to |
|---|---|
| Any **OpenAI-compatible** endpoint: a hosted API, LM Studio, vLLM or llama-server on another machine, a private gateway | **External Providers** |
| **Azure OpenAI / Azure AI Foundry**, including sign-in with **Entra ID** | **Cloud Settings** |
| **ChatGPT / Codex** with your ChatGPT subscription | **Cloud Settings** |

API keys and secrets are stored encrypted on this computer and are never shown again, not even to this page.

---

## An OpenAI-compatible connection

1. Open **Settings → External Providers** and click **Add connection**.
2. Fill in:
   - **Connection id:** a short name such as `office-gateway`. It becomes part of every model id from this
     connection and cannot be changed later.
   - **Display name:** what the model picker shows.
   - **Base URL:** the endpoint's OpenAI-compatible address, with or without the trailing `/v1`.
   - **API key or bearer token:** optional. Leave it blank for a server that needs no authentication.
   - **Custom headers:** only if your gateway asks for extra headers, such as a project or tenant header. Mark a
     value **secret** to store it write-only.
3. Choose the **Trust** of the connection (see below).
4. Click **Test connection**. The app asks the endpoint for its model list. If the endpoint lists models, pick one
   to add it. Plenty of gateways answer without a list; then type the model id yourself under **Models**.
5. For each model, say whether it supports **Tools**, **Vision** and **Reasoning**. The app does not detect this:
   only **Yes** counts, and **Unknown** is treated like **No**.
6. Click **Save connection**. The models now appear in the model picker under the connection's name.

**Plain `http` to another machine is refused** unless you tick **Allow insecure HTTP**, because the key and your
prompts would travel unencrypted. Prefer `https`. `http` to this computer itself is always fine.

### Local or Cloud: the Trust setting

- **Local (self-hosted)** means you control the machine the model runs on. Its models are trusted like a model on
  this computer: they can use your workspace files, the knowledge base, custom tools and code execution. If the
  address is not on this computer or your private network, the app warns you before you save.
- **Cloud (hosted)** means the model runs on someone else's service. The cloud rules below apply.

### Cloud permissions for this connection

A Cloud connection has its own **Cloud permissions for this connection**: five switches, all off by default, that
let this connection's models read local data, run unattended, use web tools, use MCP tools and delegate to
sub-agents. They match the five node-wide switches in the **Cloud models** card under **Node Settings → Privacy**,
and [Privacy & your data](privacy-and-data.md#only-if-you-switch-it-on) says what each one covers. A node-wide
switch applies to every cloud model; a connection's own switch applies to that connection only. Use the
connection's switches for a gateway you trust, and leave the node-wide ones off for everything else. A change takes
effect on the next message or run.

**No setting lets a cloud model run code on this computer.** Code execution, command-type custom tools, Development
Mode, training and benchmarks stay closed to cloud models whatever you switch on.

## Azure OpenAI / Azure AI Foundry

1. Open **Settings → Cloud Settings**.
2. Enter the **Azure OpenAI endpoint** and pick an **Authentication** mode:
   - **API key:** paste the key.
   - **Managed identity:** uses the Azure identity already signed in on this computer; no key needed.
   - **Entra ID:** for gateways that expect a Microsoft Entra token. Enter the **Tenant ID**, **Client ID** and
     **Token scope**. With a **Client secret** the app signs in on its own; without one, you sign in once with a
     **Device code** or in an **Interactive browser** tab.
3. Pick the **API surface**: **Azure deployments** for a normal Azure OpenAI resource, **OpenAI v1** for a Foundry
   or gateway endpoint.
4. Under **Models**, add each **Deployment name** exactly as the Azure portal shows it.
5. Click **Save cloud settings**.

The Azure connection has no per-connection permissions: the node-wide **Cloud models** switches decide what its
models may do.

## ChatGPT / Codex

In **Settings → Cloud Settings**, click **Sign in with ChatGPT** and finish the sign-in in the browser window that
opens. **Sign out** returns you to local models. The app asks the service not to store responses on every call.

---

## What leaves your computer

When you chat with a remote model, **your messages, the model's earlier replies in that conversation and the agent's
instructions go to that service**. That is how it answers. Anything else, such as document content, files, tool results or web pages, goes
only when a switch above allows it. The app shows which service a model belongs to in the model picker.

Nothing is sent until you choose a remote model, and **Test connection** only asks for the model list. The full list
of what can connect to the internet is in [Privacy & your data](privacy-and-data.md#only-if-you-switch-it-on).

---

**More detail for developers:** [Security & Privacy](../../wiki/12-security-and-privacy.md) and
[Satellite and Cloud Providers](../../wiki/reference/03-satellite-and-cloud-providers.md).

**[← Back to the main page](../README.md)**
