using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Tests.GitHub.Fakes;

namespace ForgeDesk.Core.Tests.GitHub;

public class GitHubResponseCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Values_are_reused_until_they_expire()
    {
        var clock = new FakeClock();
        var cache = new GitHubResponseCache(clock);
        var calls = 0;
        Task<int> Load() => Task.FromResult(++calls);

        (await cache.GetOrAddAsync("octo/app", "issues", Ttl, Load, Ct)).Should().Be(1);
        clock.Advance(Ttl - TimeSpan.FromMilliseconds(1));
        (await cache.GetOrAddAsync("octo/app", "issues", Ttl, Load, Ct)).Should().Be(1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await cache.GetOrAddAsync("octo/app", "issues", Ttl, Load, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Different_keys_are_cached_separately()
    {
        var cache = new GitHubResponseCache(new FakeClock());

        var open = await cache.GetOrAddAsync("octo/app", "issues:open", Ttl, () => Task.FromResult("open"), Ct);
        var closed = await cache.GetOrAddAsync("octo/app", "issues:closed", Ttl, () => Task.FromResult("closed"), Ct);

        (open, closed).Should().Be(("open", "closed"));
    }

    [Fact]
    public async Task Concurrent_callers_share_one_in_flight_request()
    {
        var cache = new GitHubResponseCache(new FakeClock());
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<int> Load()
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        }

        var callers = Enumerable.Range(0, 8).Select(_ => cache.GetOrAddAsync("octo/app", "runs", Ttl, Load, Ct)).ToList();
        gate.SetResult(42);
        var results = await Task.WhenAll(callers);

        results.Should().AllBeEquivalentTo(42);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var cache = new GitHubResponseCache(new FakeClock());
        var attempt = 0;
        Task<int> Load() => ++attempt == 1 ? Task.FromException<int>(new HttpRequestException("offline")) : Task.FromResult(attempt);

        var first = () => cache.GetOrAddAsync("octo/app", "repo", Ttl, Load, Ct);
        await first.Should().ThrowAsync<HttpRequestException>();

        (await cache.GetOrAddAsync("octo/app", "repo", Ttl, Load, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Invalidation_only_drops_the_repository_and_ignores_case()
    {
        var cache = new GitHubResponseCache(new FakeClock());
        var calls = 0;
        Task<int> Load() => Task.FromResult(++calls);
        await cache.GetOrAddAsync("octo/app", "issues", Ttl, Load, Ct);
        await cache.GetOrAddAsync("octo/other", "issues", Ttl, Load, Ct);

        cache.Invalidate("OCTO/App");

        (await cache.GetOrAddAsync("octo/app", "issues", Ttl, Load, Ct)).Should().Be(3);
        (await cache.GetOrAddAsync("octo/other", "issues", Ttl, Load, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Clear_drops_everything()
    {
        var cache = new GitHubResponseCache(new FakeClock());
        await cache.GetOrAddAsync("octo/app", "issues", Ttl, () => Task.FromResult(1), Ct);
        await cache.GetOrAddAsync(GitHubResponseCache.AccountScope, "repositories", Ttl, () => Task.FromResult(1), Ct);

        cache.Clear();

        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_caller_giving_up_does_not_cancel_the_shared_request()
    {
        var cache = new GitHubResponseCache(new FakeClock());
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var impatient = new CancellationTokenSource();

        var first = cache.GetOrAddAsync("octo/app", "releases", Ttl, () => gate.Task, impatient.Token);
        var second = cache.GetOrAddAsync("octo/app", "releases", Ttl, () => gate.Task, Ct);
        await impatient.CancelAsync();
        gate.SetResult("v1.0.0");

        var cancelled = async () => await first;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await second).Should().Be("v1.0.0");
    }

    [Fact]
    public async Task Results_of_a_request_invalidated_while_in_flight_are_not_kept()
    {
        var cache = new GitHubResponseCache(new FakeClock());
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.GetOrAddAsync("octo/app", "issues", Ttl, () => gate.Task, Ct);

        cache.Invalidate("octo/app");
        gate.SetResult(1);
        await pending;

        (await cache.GetOrAddAsync("octo/app", "issues", Ttl, () => Task.FromResult(2), Ct)).Should().Be(2);
    }
}
