# Provider validation

October 3, 2026. Live access checks; these are smoke checks, not the held-out quality evaluation.

Both credentials were loaded programmatically from the user-designated protected source. The source was not modified. No credential values are recorded here.

| Provider | Verified model | Synthetic task | Result | Usage | Wall latency |
|---|---|---|---|---|---|
| TypeSafe | `jev-1.13.0` | Distinguish a decision explicitly not shipped from observed implementation | `approved_intent` | 357 input, 50 output tokens | 242 ms |
| OpenAI Responses | `gpt-6-luna` | Same distinction with strict JSON schema, `store:false` | `approved_intent`, completed | 62 input, 43 output tokens (25 reasoning) | 2505 ms |

Model identifiers were verified through the providers' authenticated model listings, separately from Codex builder model selection. Initial generative model is `gpt-6-luna`; any stronger route requires measured justification. Jev is pinned rather than using a moving alias.

Official reference checks: [TypeSafe API](https://docs.typesafe.ai/api), [TypeSafe model/version and prices](https://docs.typesafe.ai/models), [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs), [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna), [GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol).

At inspection, published standard per-million rates were Luna $0.10 input / $0.50 output, Sol $2 input / $10 output, and Jev $0.042 input / free output. Configured estimates must account conservatively for cache-write premiums, repeated requests and missing usage. Estimates are not provider billing guarantees. Main-agent context reduction is measured separately from total provider cost.

Still required: deployed Unraid adapter verification, failure classification, fixed direct-workflow baseline, held-out semantic quality evaluation, complete per-request usage/latency evidence.
