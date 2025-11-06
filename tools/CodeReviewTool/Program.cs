// <copyright file="Program.cs" company="CodeReviewDot">
// Copyright (c) CodeReviewDot contributors. All rights reserved.
// Licensed under the MIT License.
// </copyright>
using System.ClientModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json; // used for possible future parsing/extensions
using Octokit;
using OpenAI;
using OpenAI.Chat;

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

            // Collect diff context for AI model (limit size to avoid excessive token usage)
            var diffBuilder = new StringBuilder();
            foreach (var f in files)
            {
                if (f.Patch is null)
                {
                    continue;
                }
                diffBuilder.AppendLine($"# File: {f.FileName}");
                // Truncate overly large patches
                var patch = f.Patch;
                if (patch.Length > 8000)
                {
                    patch = patch.Substring(0, 8000) + "\n...[truncated]";
                }
                diffBuilder.AppendLine(patch);
                diffBuilder.AppendLine();
            }
            var fullDiff = diffBuilder.ToString();

            // Load prompt template
            string promptPath = Path.Combine(Directory.GetCurrentDirectory(), "review-prompt.md");
            string promptTemplate = File.Exists(promptPath)
                ? File.ReadAllText(promptPath)
                : "You are a code review assistant. Provide findings and suggestions.";

            Console.WriteLine("Prompt Path exists: " + File.Exists(promptPath));

            // Compose model input
            var modelName = Environment.GetEnvironmentVariable("MODEL_NAME") ?? "openai/gpt-4o"; // user can override
            var modelEndpoint = Environment.GetEnvironmentVariable("MODEL_ENDPOINT") ?? "https://models.github.ai/inference"; // placeholder endpoint; user should set actual
            var aiToken = Environment.GetEnvironmentVariable("AI_TOKEN") ?? githubToken;

            string aiBody = string.Empty;
            bool aiSucceeded = false;
            try
            {
                var openAIOptions = new OpenAIClientOptions()
                {
                    Endpoint = new Uri(modelEndpoint)

                };

                var chatClient = new ChatClient(modelName, new ApiKeyCredential(aiToken), openAIOptions);
                List<ChatMessage> messages = new List<ChatMessage>()
                {
                    new SystemChatMessage(promptTemplate),
                    new UserChatMessage(fullDiff),
                };

                var requestOptions = new ChatCompletionOptions()
                {
                    Temperature = 1.0f,
                    TopP = 1.0f,
                    MaxOutputTokenCount = 1000
                };

                var response = chatClient.CompleteChat(messages, requestOptions);
                var textResponse = (response.Value.Content as ChatMessageContent)[0].Text;
                if (!string.IsNullOrWhiteSpace(textResponse))
                {
                    aiBody = textResponse.Trim();
                    aiSucceeded = true;
                }
                else
                {
                    Console.WriteLine("AI SDK returned empty response; using heuristic fallback.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("AI SDK invocation error: " + ex.Message);
            }

            // Heuristic fallback (previous logic simplified)
            var heuristicFindings = new List<string>();
            if (!aiSucceeded)
            {
                foreach (var f in files)
                {
                    if (f.Patch == null)
                    {
                        continue;
                    }
                    var patchLines = f.Patch.Split('\n');
                    int addedLines = patchLines.Count(l => l.StartsWith("+"));
                    if (addedLines > 400)
                    {
                        heuristicFindings.Add($"File `{f.FileName}` adds {addedLines} lines. Consider splitting into smaller changes.");
                    }
                    foreach (var l in patchLines.Where(l => l.StartsWith("+")))
                    {
                        if (l.Contains("TODO"))
                        {
                            heuristicFindings.Add($"`{f.FileName}` newly added TODO; create issue reference or complete before merge.");
                        }
                        if (l.Contains("Console.WriteLine"))
                        {
                            heuristicFindings.Add($"`{f.FileName}` uses Console.WriteLine; prefer structured logging (ILogger).");
                        }
                    }
                }
            }
            string body;
            if (aiSucceeded)
            {
                body = aiBody;
            }
            else if (heuristicFindings.Count == 0)
            {
                body = "✅ No automated review comments found. (AI unavailable; heuristic scan clean.)";
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("👋 Automated Heuristic Review (AI unavailable):");
                int i = 1;
                foreach (var fnd in heuristicFindings.Distinct())
                {
                    sb.AppendLine($"{i}. {fnd}");
                    i++;
                }
                sb.AppendLine();
                body = sb.ToString();
            }

            {
                try
                {
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

            // Determine presence of findings (AI or heuristic) for labels
            bool hasFindings = aiSucceeded ? !body.Contains("No significant issues", StringComparison.OrdinalIgnoreCase) && !body.Contains("✅ No", StringComparison.OrdinalIgnoreCase) : heuristicFindings.Count > 0;
            if (!hasFindings)
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
