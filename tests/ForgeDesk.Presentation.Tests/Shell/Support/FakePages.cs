using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Tests.Shell.Support;

/// <summary>A top-level page that records navigation calls.</summary>
public sealed class FakePage(PageKind kind) : INavigationAware, IDisposable
{
    public PageKind Kind { get; } = kind;

    public List<object?> NavigatedTo { get; } = [];

    public int NavigatedFromCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task OnNavigatedToAsync(object? argument)
    {
        NavigatedTo.Add(argument);
        return Task.CompletedTask;
    }

    public void OnNavigatedFrom() => NavigatedFromCount++;

    public void Dispose() => IsDisposed = true;
}

/// <summary>Creates <see cref="FakePage"/>s for the kinds it was given; the others are unavailable.</summary>
public sealed class FakePageFactory(params PageKind[] available) : IPageFactory
{
    private readonly HashSet<PageKind> _available = [.. available];

    public List<FakePage> Created { get; } = [];

    public bool IsAvailable(PageKind kind) => _available.Contains(kind);

    public object? Create(PageKind kind)
    {
        if (!_available.Contains(kind))
        {
            return null;
        }

        var page = new FakePage(kind);
        Created.Add(page);
        return page;
    }

    public FakePage Page(PageKind kind) => Created.Single(p => p.Kind == kind);
}
