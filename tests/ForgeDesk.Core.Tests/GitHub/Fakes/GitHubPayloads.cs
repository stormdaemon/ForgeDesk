using System.Globalization;
using System.Text.Json;

namespace ForgeDesk.Core.Tests.GitHub.Fakes;

/// <summary>Minimal but realistic GitHub REST payloads (field names and shapes as documented).</summary>
public static class GitHubPayloads
{
    public const string ClassicToken = "ghp_16C7e42F292c6912E7710c838347Ae178B4a";
    public const string OtherClassicToken = "ghp_aaaaBBBBccccDDDDeeeeFFFFgggg00001111";
    public const string FineGrainedToken = "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghij";
    public const string OAuthToken = "gho_16C7e42F292c6912E7710c838347Ae178B4a";

    public static string User(string login = "octocat", string? name = "The Octocat", long id = 583231) => $$"""
        {
          "login": "{{login}}",
          "id": {{id}},
          "node_id": "MDQ6VXNlcjU4MzIzMQ==",
          "avatar_url": "https://avatars.githubusercontent.com/u/{{id}}?v=4",
          "html_url": "https://github.com/{{login}}",
          "type": "User",
          "site_admin": false,
          "name": {{Str(name)}},
          "public_repos": 8,
          "followers": 20,
          "following": 9,
          "created_at": "2011-01-25T18:44:36Z",
          "updated_at": "2026-09-01T10:00:00Z"
        }
        """;

    public static string Repository(
        string owner = "octo",
        string name = "app",
        string? pushedAt = "2026-09-20T08:00:00Z",
        bool isPrivate = false,
        bool push = true,
        string defaultBranch = "main",
        string? language = "C#") => $$"""
        {
          "id": {{1296269 + name.Length}},
          "node_id": "R_kgDOAAAAAA",
          "name": "{{name}}",
          "full_name": "{{owner}}/{{name}}",
          "private": {{Bool(isPrivate)}},
          "owner": {{User(owner, null, 1)}},
          "html_url": "https://github.com/{{owner}}/{{name}}",
          "description": "Sample repository",
          "fork": false,
          "url": "https://api.github.com/repos/{{owner}}/{{name}}",
          "clone_url": "https://github.com/{{owner}}/{{name}}.git",
          "ssh_url": "git@github.com:{{owner}}/{{name}}.git",
          "homepage": null,
          "language": {{Str(language)}},
          "forks_count": 3,
          "stargazers_count": 42,
          "watchers_count": 42,
          "open_issues_count": 5,
          "default_branch": "{{defaultBranch}}",
          "archived": false,
          "visibility": "{{(isPrivate ? "private" : "public")}}",
          "pushed_at": {{Str(pushedAt)}},
          "created_at": "2020-01-01T00:00:00Z",
          "updated_at": "2026-09-21T09:00:00Z",
          "permissions": { "admin": false, "maintain": false, "push": {{Bool(push)}}, "triage": true, "pull": true }
        }
        """;

    public static string Label(string name, string color) => $$"""
        { "id": 208045946, "node_id": "MDU6TGFiZWwyMDgwNDU5NDY=", "url": "https://api.github.com/repos/octo/app/labels/{{name}}", "name": "{{name}}", "description": null, "color": "{{color}}", "default": false }
        """;

    public static string Issue(int number, string title = "Something broke", bool open = true, bool isPullRequest = false, string labels = "[]", string author = "octocat")
    {
        // The issues endpoint marks pull requests with a "pull_request" object.
        var pullRequest = isPullRequest
            ? $$""", "pull_request": { "url": "https://api.github.com/repos/octo/app/pulls/{{number}}", "html_url": "https://github.com/octo/app/pull/{{number}}" }"""
            : string.Empty;
        return $$"""
        {
          "id": {{1000 + number}},
          "node_id": "I_kwDOAAAA",
          "url": "https://api.github.com/repos/octo/app/issues/{{number}}",
          "html_url": "https://github.com/octo/app/{{(isPullRequest ? "pull" : "issues")}}/{{number}}",
          "number": {{number}},
          "state": "{{(open ? "open" : "closed")}}",
          "title": "{{title}}",
          "body": "Steps to reproduce…",
          "user": {{User(author, null, 2)}},
          "labels": {{labels}},
          "assignees": [ {{User("hubot", null, 3)}} ],
          "comments": 2,
          "locked": false,
          "created_at": "2026-09-10T12:00:00Z",
          "updated_at": "2026-09-11T12:00:00Z",
          "closed_at": {{(open ? "null" : "\"2026-09-12T12:00:00Z\"")}}{{pullRequest}}
        }
        """;
    }

    public static string PullRequest(
        int number,
        string state = "open",
        bool draft = false,
        bool merged = false,
        string headSha = "6dcb09b5b57875f334f61aebed695e2e4193db5e",
        string head = "feature/login",
        string baseBranch = "main",
        bool? mergeable = true,
        int additions = 0,
        int deletions = 0,
        int changedFiles = 0) => $$"""
        {
          "id": {{5000 + number}},
          "node_id": "PR_kwDOAAAA",
          "url": "https://api.github.com/repos/octo/app/pulls/{{number}}",
          "html_url": "https://github.com/octo/app/pull/{{number}}",
          "number": {{number}},
          "state": "{{state}}",
          "locked": false,
          "title": "Add login page",
          "body": "Implements the login page.",
          "user": {{User("octocat", null, 2)}},
          "labels": [ {{Label("enhancement", "A2EEEF")}} ],
          "draft": {{Bool(draft)}},
          "merged": {{Bool(merged)}},
          "mergeable": {{(mergeable is null ? "null" : Bool(mergeable.Value))}},
          "merged_at": {{(merged ? "\"2026-09-15T10:00:00Z\"" : "null")}},
          "comments": 4,
          "commits": 3,
          "additions": {{additions}},
          "deletions": {{deletions}},
          "changed_files": {{changedFiles}},
          "requested_reviewers": [ {{User("reviewer", null, 7)}} ],
          "requested_teams": [],
          "head": { "label": "octo:{{head}}", "ref": "{{head}}", "sha": "{{headSha}}", "user": {{User("octo", null, 1)}} },
          "base": { "label": "octo:{{baseBranch}}", "ref": "{{baseBranch}}", "sha": "0000000000000000000000000000000000000000", "user": {{User("octo", null, 1)}} },
          "created_at": "2026-09-14T09:00:00Z",
          "updated_at": "2026-09-14T11:00:00Z",
          "closed_at": {{(state == "closed" ? "\"2026-09-15T10:00:00Z\"" : "null")}}
        }
        """;

    public static string WorkflowRun(
        long id,
        string name,
        long workflowId,
        string status,
        string? conclusion,
        string createdAt = "2026-09-20T10:00:00Z",
        string branch = "main",
        string headSha = "abc123",
        int runNumber = 1,
        string commitMessage = "Fix the build\n\nLonger description") => $$"""
        {
          "id": {{id}},
          "name": "{{name}}",
          "node_id": "WFR_kwLOAAAA",
          "head_branch": "{{branch}}",
          "head_sha": "{{headSha}}",
          "path": ".github/workflows/{{name.ToLowerInvariant()}}.yml",
          "display_title": "Fix the build",
          "run_number": {{runNumber}},
          "run_attempt": 1,
          "event": "push",
          "status": "{{status}}",
          "conclusion": {{Str(conclusion)}},
          "workflow_id": {{workflowId}},
          "check_suite_id": 42,
          "url": "https://api.github.com/repos/octo/app/actions/runs/{{id}}",
          "html_url": "https://github.com/octo/app/actions/runs/{{id}}",
          "created_at": "{{createdAt}}",
          "updated_at": "{{Shift(createdAt, 5)}}",
          "run_started_at": "{{createdAt}}",
          "actor": {{User("octocat", null, 2)}},
          "triggering_actor": {{User("octocat", null, 2)}},
          "head_commit": { "id": "{{headSha}}", "tree_id": "t1", "message": {{Str(commitMessage)}}, "timestamp": "{{createdAt}}", "author": { "name": "Mona", "email": "mona@example.com" }, "committer": { "name": "Mona", "email": "mona@example.com" } }
        }
        """;

    public static string WorkflowRuns(params string[] runs) => $$"""
        { "total_count": {{runs.Length}}, "workflow_runs": [ {{string.Join(",", runs)}} ] }
        """;

    public static string Workflows() => """
        {
          "total_count": 2,
          "workflows": [
            { "id": 161335, "node_id": "MDg6V29ya2Zsb3cxNjEzMzU=", "name": "CI", "path": ".github/workflows/ci.yml", "state": "active", "created_at": "2020-01-08T23:48:37Z", "updated_at": "2020-01-08T23:50:21Z", "url": "https://api.github.com/repos/octo/app/actions/workflows/161335", "html_url": "https://github.com/octo/app/blob/main/.github/workflows/ci.yml", "badge_url": "https://github.com/octo/app/workflows/CI/badge.svg" },
            { "id": 161336, "node_id": "MDg6V29ya2Zsb3cxNjEzMzY=", "name": "Release", "path": ".github/workflows/release.yml", "state": "disabled_manually", "created_at": "2020-01-08T23:48:37Z", "updated_at": "2020-01-08T23:50:21Z", "url": "https://api.github.com/repos/octo/app/actions/workflows/161336", "html_url": "https://github.com/octo/app/blob/main/.github/workflows/release.yml", "badge_url": "https://github.com/octo/app/workflows/Release/badge.svg" }
          ]
        }
        """;

    public static string Jobs() => """
        {
          "total_count": 1,
          "jobs": [
            {
              "id": 399444496, "run_id": 29679449, "node_id": "MDEyOldvcmtmbG93Sm9iMzk5NDQ0NDk2",
              "head_sha": "abc123", "url": "https://api.github.com/repos/octo/app/actions/jobs/399444496",
              "html_url": "https://github.com/octo/app/runs/399444496",
              "status": "in_progress", "conclusion": null,
              "created_at": "2026-09-20T10:00:00Z", "started_at": "2026-09-20T10:00:10Z", "completed_at": null,
              "name": "build",
              "steps": [
                { "name": "Run tests", "status": "in_progress", "conclusion": null, "number": 2, "started_at": "2026-09-20T10:01:00Z", "completed_at": null },
                { "name": "Set up job", "status": "completed", "conclusion": "success", "number": 1, "started_at": "2026-09-20T10:00:10Z", "completed_at": "2026-09-20T10:00:20Z" }
              ],
              "check_run_url": "https://api.github.com/repos/octo/app/check-runs/399444496",
              "labels": [ "windows-latest" ], "runner_id": 1, "runner_name": "GitHub Actions 1", "runner_group_id": 2, "runner_group_name": "GitHub Actions"
            }
          ]
        }
        """;

    public static string CheckRuns(params (string Status, string? Conclusion)[] runs)
    {
        var items = runs.Select((r, i) => CheckRun(i + 1, r.Status, r.Conclusion));
        return $$"""{ "total_count": {{runs.Length}}, "check_runs": [ {{string.Join(",", items)}} ] }""";
    }

    public static string CheckRun(int id, string status, string? conclusion) => $$"""
        {
          "id": {{id}}, "head_sha": "6dcb09b5b57875f334f61aebed695e2e4193db5e", "node_id": "CR_{{id}}", "external_id": "",
          "url": "https://api.github.com/repos/octo/app/check-runs/{{id}}", "html_url": "https://github.com/octo/app/runs/{{id}}",
          "details_url": "https://example.com", "status": "{{status}}", "conclusion": {{Str(conclusion)}},
          "started_at": "2026-09-14T09:00:00Z", "completed_at": null, "name": "check {{id}}",
          "output": { "title": null, "summary": null, "text": null, "annotations_count": 0, "annotations_url": "" },
          "pull_requests": []
        }
        """;

    public static string CombinedStatus(params string[] states)
    {
        var items = states.Select((s, i) => CommitStatus(i + 1, s));
        return $$"""
            {
              "state": "{{(states.Length == 0 ? "pending" : states[0])}}",
              "sha": "6dcb09b5b57875f334f61aebed695e2e4193db5e",
              "total_count": {{states.Length}},
              "statuses": [ {{string.Join(",", items)}} ]
            }
            """;
    }

    public static string CommitStatus(int id, string state) => $$"""
        {
          "id": {{id}}, "node_id": "SC_{{id}}", "url": "https://api.github.com/repos/octo/app/statuses/1", "state": "{{state}}",
          "description": "Build", "target_url": "https://ci.example.com/{{id}}", "context": "ci/external-{{id}}",
          "created_at": "2026-09-14T09:00:00Z", "updated_at": "2026-09-14T09:05:00Z"
        }
        """;

    public static string Asset(long id, string name, long size = 1024, string contentType = "application/zip") => $$"""
        {
          "url": "https://api.github.com/repos/octo/app/releases/assets/{{id}}",
          "browser_download_url": "https://github.com/octo/app/releases/download/v1.0.0/{{name}}",
          "id": {{id}}, "node_id": "RA_kwDOAAAA", "name": "{{name}}", "label": "", "state": "uploaded",
          "content_type": "{{contentType}}", "size": {{size}}, "download_count": 7,
          "created_at": "2026-09-01T10:00:00Z", "updated_at": "2026-09-01T10:00:00Z",
          "uploader": {{User("octocat", null, 2)}}
        }
        """;

    public static string UploadUrl(long releaseId) => "https://uploads.github.test/repos/octo/app/releases/" + releaseId + "/assets{?name,label}";

    public static string Release(long id, string tag, bool draft = false, bool prerelease = false, string assets = "[]") => $$"""
        {
          "url": "https://api.github.com/repos/octo/app/releases/{{id}}",
          "html_url": "https://github.com/octo/app/releases/tag/{{tag}}",
          "assets_url": "https://api.github.com/repos/octo/app/releases/{{id}}/assets",
          "upload_url": "{{UploadUrl(id)}}",
          "tarball_url": "https://api.github.com/repos/octo/app/tarball/{{tag}}",
          "zipball_url": "https://api.github.com/repos/octo/app/zipball/{{tag}}",
          "id": {{id}}, "node_id": "RE_kwDOAAAA",
          "tag_name": "{{tag}}", "target_commitish": "main", "name": "Release {{tag}}",
          "body": "## What's changed\n- Fixes",
          "draft": {{Bool(draft)}}, "prerelease": {{Bool(prerelease)}},
          "created_at": "2026-09-01T10:00:00Z",
          "published_at": {{(draft ? "null" : "\"2026-09-01T11:00:00Z\"")}},
          "author": {{User("octocat", null, 2)}},
          "assets": {{assets}}
        }
        """;

    public static string Comment(long id, string body) => $$"""
        {
          "id": {{id}}, "node_id": "IC_kwDOAAAA",
          "url": "https://api.github.com/repos/octo/app/issues/comments/{{id}}",
          "html_url": "https://github.com/octo/app/issues/1#issuecomment-{{id}}",
          "body": {{Str(body)}},
          "user": {{User("octocat", null, 2)}},
          "created_at": "2026-09-11T12:00:00Z", "updated_at": "2026-09-11T12:00:00Z",
          "author_association": "OWNER"
        }
        """;

    public static string RateLimit(int limit, int remaining, long resetEpoch) => $$"""
        {
          "resources": {
            "core": { "limit": {{limit}}, "used": {{limit - remaining}}, "remaining": {{remaining}}, "reset": {{resetEpoch}} },
            "search": { "limit": 30, "used": 0, "remaining": 30, "reset": {{resetEpoch}} },
            "graphql": { "limit": 5000, "used": 0, "remaining": 5000, "reset": {{resetEpoch}} }
          },
          "rate": { "limit": {{limit}}, "used": {{limit - remaining}}, "remaining": {{remaining}}, "reset": {{resetEpoch}} }
        }
        """;

    public static string Array(IEnumerable<string> items) => $"[{string.Join(",", items)}]";

    private static string Str(string? value) => value is null ? "null" : JsonSerializer.Serialize(value);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Shift(string timestamp, int minutes) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture).AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
