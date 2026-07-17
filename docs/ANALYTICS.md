# Local analytics

QuotaBeacon derives Codex activity analytics from local JSONL metadata. It
does not call a billing API and the dollar figures are not Codex subscription
charges.

## Inputs and privacy

The reader scans recent files under `CODEX_HOME\sessions` (or
`~\.codex\sessions`) and prefilters for only two record types:

- `turn_context.model` attributes subsequent usage to a model;
- `token_count.info.last_token_usage` supplies input, cached-input, output,
  reasoning-output, and total-token counts.

Message, response, reasoning, tool-call, prompt, and file-content records are
skipped before JSON parsing. Analytics are projected in memory and are not
added to quota history. One unreadable or partially written session file is
isolated from the rest.

## Metric definitions

- **Today / 30-day tokens:** sum of `last_token_usage.total_tokens` in the
  corresponding local calendar window.
- **Sessions:** distinct session files containing usage in the window.
- **Cache rate:** cached input divided by total input for today.
- **Model share:** a model's tokens divided by all 30-day tokens.
- **Active days:** days with at least one token-count event.
- **Quota pace:** provider usage percentage minus elapsed-window percentage.
  Positive values are ahead of budget; negative values are under budget.

## Cost estimates

Estimated cost uses the uncached input, cached input, and output token rates in
OpenAI's standard API model catalog. Rates were verified on 2026-07-17 for
GPT-5.6 Sol/Terra/Luna, GPT-5.5, and GPT-5.4:

- https://developers.openai.com/api/docs/models
- https://developers.openai.com/api/docs/models/gpt-5.5
- https://developers.openai.com/api/docs/models/gpt-5.4

The formula is:

`uncached input × input rate + cached input × cached rate + output × output rate`

All rates are normalized per one million tokens. An estimate is prefixed with
`~` when any observed model lacks a verified exact/snapshot price. The catalog
does not apply a base-model price to mini, pro, or other tiers by prefix.
