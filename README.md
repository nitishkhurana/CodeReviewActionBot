# Automated .NET Code Review Action

This repository includes a GitHub Action workflow (`.github/workflows/code-review.yml`) and a small .NET console tool (`tools/CodeReviewTool`) that performs lightweight automated code review on every Pull Request.

## What It Does
- Automatically triggers on PR open/update.
- Inspects changed files for simple heuristics: excessive added lines, TODO comments, and `Console.WriteLine` usage.
- Posts or updates a single review comment with polite, junior-developer–friendly guidance and concrete suggestions.
- Applies labels:
  - `Changes Requested` (yellow) if any suggestions are found.
  - `Ready for Review` (green) if no suggestions remain.
- Keeps labels in sync as the PR evolves (removes old label, adds the new one).

## Labels
The tool ensures labels exist (with colors) and updates them if color changes:
- `Changes Requested`: `#f9d71c`
- `Ready for Review`: `#28a745`

## Extending the Review
The action now supports AI-assisted review plus a heuristic fallback.

### AI Model Review
The workflow will:
1. Aggregate the PR diff (truncating very large patches).
2. Load the prompt template from `review-prompt.md`.
3. Send both prompt and diff to a configurable model endpoint.
4. Use the model's Markdown response directly as the PR comment.
5. If the model call fails, it falls back to simple heuristics (large additions, TODO, `Console.WriteLine`).

Configure via environment variables (set them in the workflow if different from defaults):
- `MODEL_NAME` (default: `openai/gpt-4o` placeholder – replace with your actual GitHub Model ID)
- `MODEL_ENDPOINT` (default: `https://models.github.ai/inference` placeholder – update to the correct endpoint for GitHub Models or provider)
- `AI_TOKEN` (optional – uses `GITHUB_TOKEN` if not set). Provide a separate token if the model requires different scopes.

Edit `review-prompt.md` to change tone, structure, or rules without recompiling.

### Further Enhancements
1. Parse analyzer diagnostics and feed them into the prompt.
2. Add complexity metrics (Roslynator) and summarize hotspots.
3. Include `dotnet format --verify-no-changes` for style enforcement pre-review.
4. Add caching to skip AI call when diff unchanged.
5. Provide inline review comments using PR Review API when model returns line-level data.

## Local Development
```
# Restore
(dotnet restore)
# Run tool manually (requires GITHUB_TOKEN and event JSON)
# Simulate by creating a minimal event file containing: {"number": <prNumber>} and setting env vars.
```

## Security Notes
- Uses the built-in `GITHUB_TOKEN` scoped by workflow permissions.
- Only writes PR comments and labels.

## Future Ideas
- Add configuration file (e.g., `.codereviewconfig.json`) to control thresholds & model settings.
- Surface analyzer diagnostics with severity filtering.
- Provide inline review comments at specific lines using the Pull Request Review API.
- Add unit tests around diff parsing & AI response classification.
- Cache previous review results to minimize duplicate work.

## Troubleshooting
| Issue | Possible Cause | Fix |
|-------|----------------|-----|
| No comment posted | Missing `GITHUB_TOKEN` or event path | Ensure workflow has `pull-requests: write` permission |
| Labels not updated | Missing repo permissions | Check repository settings |
| Build fails | Analyzer warnings, missing SDK | Adjust analyzer settings or install correct .NET version |

---
Feel free to adapt and grow this into a more comprehensive automated reviewer.
