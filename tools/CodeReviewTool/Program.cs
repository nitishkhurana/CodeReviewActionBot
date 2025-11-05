// <copyright file="Program.cs" company="CodeReviewDot">
// Copyright (c) CodeReviewDot contributors. All rights reserved.
// Licensed under the MIT License.
// </copyright>
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Octokit;

/// <summary>
/// Entry point for automated PR code review action.
/// </summary>
internal static class Program
{
    // Labels
    private const string ChangesRequestedLabel = "Changes Requested"; // yellow
    private const string ReadyForReviewLabel = "Ready for Review"; // green

    public static async Task<int> Main()
    {
        try
        {
            var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                Console.WriteLine("GITHUB_TOKEN not available.");
                return 1;
            }

            var eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
            if (string.IsNullOrWhiteSpace(eventPath) || !File.Exists(eventPath))
            {
                Console.WriteLine("GITHUB_EVENT_PATH missing.");
                return 1;
            }

            dynamic evt = Newtonsoft.Json.JsonConvert.DeserializeObject(File.ReadAllText(eventPath))!;
            int prNumber = (int)evt.number;
            string repoFull = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? string.Empty;
            if (string.IsNullOrEmpty(repoFull) || !repoFull.Contains('/'))
            {
                Console.WriteLine("GITHUB_REPOSITORY invalid.");
                return 1;
            }
            var parts = repoFull.Split('/');
            string owner = parts[0];
            string repo = parts[1];

            var client = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("dotnet-code-review"))
            {
                Credentials = new Credentials(githubToken),
            };

            var pr = await client.PullRequest.Get(owner, repo, prNumber);
            var files = await client.PullRequest.Files(owner, repo, prNumber);

            // Basic heuristic review: naming, large files, TODO comments.
            var comments = new List<string>();
            foreach (var f in files)
            {
                if (f.Status != "added" && f.Status != "modified")
                {
                    continue;
                }

                // Only inspect text based
                if (f.Patch == null)
                {
                    continue;
                }
                var patchLines = f.Patch.Split('\n');

                int addedLines = patchLines.Count(l => l.StartsWith("+"));
                if (addedLines > 400)
                {
                    comments.Add($"File `{f.FileName}` adds {addedLines} lines. Consider splitting into smaller, focused changes to aid review.");
                }

                foreach (var l in patchLines.Where(l => l.StartsWith("+")))
                {
                    if (l.Contains("TODO"))
                    {
                        comments.Add($"`{f.FileName}` contains a TODO in newly added code. Prefer creating an issue and referencing it, or completing the task before merge.");
                    }

                    if (l.Contains("Console.WriteLine"))
                    {
                        comments.Add($"`{f.FileName}` uses Console.WriteLine in new code. For production or library code, consider using a structured logger (e.g., ILogger) so logs are configurable and testable.");
                    }
                }
            }

            // Detect unresolved previous comments pattern? (Simplified: we re-run every time.)
            string body;
            if (comments.Count == 0)
            {
                body = "✅ No automated review comments found.";// Nice work! The changes look clean and focused. If you would like more thorough static analysis, consider adding Roslyn analyzers or StyleCop for deeper checks.";
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("👋 Automated Code Review Suggestions (early guidance):");
                sb.AppendLine();
                int i = 1;
                foreach (var c in comments.Distinct())
                {
                    sb.AppendLine($"{i}. {c}");

                    // Suggestion attempt
                    if (c.Contains("Console.WriteLine"))
                    {
                        sb.AppendLine("   Suggestion: Inject an ILogger (e.g., via dependency injection) and replace Console.WriteLine with logger.LogInformation or appropriate level.");
                    }

                    if (c.Contains("TODO"))
                    {
                        sb.AppendLine("   Suggestion: Create a GitHub issue describing the pending work and replace the TODO with a reference comment like // TODO(issue-123): <summary>.");
                    }

                    if (c.Contains("adds "))
                    {
                        sb.AppendLine("   Suggestion: Break this file or change set into smaller PRs focusing on one concern each. This improves review depth and reduces merge risk.");
                    }

                    sb.AppendLine();
                    i++;
                }
                sb.AppendLine("Please treat these as friendly guidance to improve clarity, maintainability, and scalability.");
                body = sb.ToString();
            }

            // Find existing bot comment to update instead of duplicating
            //var issueComments = await client.Issue.Comment.GetAllForIssue(owner, repo, prNumber, ApiOptions.None);
            //var existing = issueComments.FirstOrDefault(c => c.User.Login.Equals(pr.User.Login, StringComparison.OrdinalIgnoreCase) == false && c.Body != null && c.Body.Contains("Automated Code Review Suggestions"));
            //if (existing != null)
            // {
            //     await client.Issue.Comment.Update(owner, repo, existing.Id, body);
            // }
            // else
            {
                try
                {
                    await client.Issue.Comment.Create(owner, repo, prNumber, body);
                }
                catch (OverflowException ovEx)
                {
                    Console.WriteLine("OverflowException when using Octokit to create comment. Falling back to direct REST API. Details: " + ovEx.Message);
                    // Fallback REST call
                    using var http = new HttpClient();
                    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dotnet-code-review", "1.0"));
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
                    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    var payload = System.Text.Json.JsonSerializer.Serialize(new { body });
                    var resp = await http.PostAsync($"https://api.github.com/repos/{owner}/{repo}/issues/{prNumber}/comments", new StringContent(payload, Encoding.UTF8, "application/json"));
                    if (!resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Fallback REST comment creation failed. Status: {resp.StatusCode}. Response: {await resp.Content.ReadAsStringAsync()}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Unexpected error creating comment via Octokit: " + ex);
                    return 1;
                }
            }

            // Manage labels
            var currentLabels = pr.Labels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            
            // Track whether we changed labels (currently only for potential future logging)
            bool changed = false;

            if (comments.Count == 0)
            {
                // Remove Changes Requested if present
                if (currentLabels.Contains(ChangesRequestedLabel))
                {
                    await client.Issue.Labels.RemoveFromIssue(owner, repo, prNumber, ChangesRequestedLabel);
                    changed = true;
                }

                if (!currentLabels.Contains(ReadyForReviewLabel))
                {
                    await client.Issue.Labels.AddToIssue(owner, repo, prNumber, new[] { ReadyForReviewLabel });
                    changed = true;
                }
            }
            else
            {
                if (currentLabels.Contains(ReadyForReviewLabel))
                {
                    await client.Issue.Labels.RemoveFromIssue(owner, repo, prNumber, ReadyForReviewLabel);
                    changed = true;
                }

                if (!currentLabels.Contains(ChangesRequestedLabel))
                {
                    await client.Issue.Labels.AddToIssue(owner, repo, prNumber, new[] { ChangesRequestedLabel });
                    changed = true;
                }
            }

            // Ensure label colors (create/update)
            var repoLabels = await client.Issue.Labels.GetAllForRepository(owner, repo);
            async Task EnsureLabel(string name, string color, string description)
            {
                var existingLabel = repoLabels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existingLabel == null)
                {
                    await client.Issue.Labels.Create(owner, repo, new NewLabel(name, color) { Description = description });
                }
                else if (!existingLabel.Color.Equals(color, StringComparison.OrdinalIgnoreCase) || existingLabel.Description != description)
                {
                    // Octokit label update helper signature ambiguity; perform direct REST patch to ensure color/description.
                    using var http = new HttpClient();
                    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dotnet-code-review", "1.0"));
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
                    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    var payload = System.Text.Json.JsonSerializer.Serialize(new { color, description });
                    var method = new HttpMethod("PATCH");
                    var request = new HttpRequestMessage(method, $"https://api.github.com/repos/{owner}/{repo}/labels/{Uri.EscapeDataString(name)}")
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    var resp = await http.SendAsync(request);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Warning: Failed to update label '{name}'. Status: {resp.StatusCode}");
                    }
                }
            }

            await EnsureLabel(ChangesRequestedLabel, "f9d71c", "Automated review found suggestions to address");
            await EnsureLabel(ReadyForReviewLabel, "28a745", "Automated review found no issues");

            if (changed)
            {
                Console.WriteLine("Labels were updated to reflect review state.");
            }
            Console.WriteLine("Code review action completed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            Console.WriteLine(ex);
            return 1;
        }
    }
}
