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
Currently the review is heuristic-based. You can extend it by:
1. Adding Roslyn analyzers or StyleCop rules (already referenced globally in `Directory.Build.props`).
2. Parsing analyzer diagnostics and including them in suggestions.
3. Adding complexity metrics (e.g., cyclomatic complexity) via tools like [Roslynator](https://github.com/JosefPihrt/Roslynator).
4. Including `dotnet format --verify-no-changes` to enforce style.

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
- Add configuration file (e.g., `.codereviewconfig.json`) to control rules.
- Surface analyzer diagnostics with severity filtering.
- Provide inline review comments at specific lines using the Pull Request Review API.
- Add unit tests around parsing logic.
- Cache previous review results to minimize duplicate work.

## Troubleshooting
| Issue | Possible Cause | Fix |
|-------|----------------|-----|
| No comment posted | Missing `GITHUB_TOKEN` or event path | Ensure workflow has `pull-requests: write` permission |
| Labels not updated | Missing repo permissions | Check repository settings |
| Build fails | Analyzer warnings, missing SDK | Adjust analyzer settings or install correct .NET version |

---
Feel free to adapt and grow this into a more comprehensive automated reviewer.
