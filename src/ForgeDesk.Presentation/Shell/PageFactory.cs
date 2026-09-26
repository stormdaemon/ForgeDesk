using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Shell;

/// <summary>Resolves page view models from <see cref="PageRegistration"/>s (the last registration of a kind wins).</summary>
public sealed class PageFactory : IPageFactory
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<PageKind, Type> _types = [];

    public PageFactory(IServiceProvider services, IEnumerable<PageRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _services = services;
        foreach (var registration in registrations)
        {
            _types[registration.Kind] = registration.ViewModelType;
        }
    }

    public bool IsAvailable(PageKind kind) => _types.ContainsKey(kind);

    public object? Create(PageKind kind) =>
        _types.TryGetValue(kind, out var type) ? ActivatorUtilities.GetServiceOrCreateInstance(_services, type) : null;
}
