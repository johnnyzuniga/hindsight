# Local models

Put GGUF model files here. They're git-ignored. The Agent and the evals pick the first `.gguf` in this folder whose name doesn't contain "embed".

Recommended files:

- **Default:** Qwen3-4B-Instruct-2507, Q4_K_M (~2.5 GB), Apache 2.0 license.
- **Lite tier, for 8 GB machines:** Qwen3-1.7B, Q4_K_M (~1.1 GB).

You can also point at a model elsewhere:

- with the `RESMAN_MODEL` environment variable, or
- with `Inference:ModelPath` in `src/ResMan.Agent/appsettings.json`.
