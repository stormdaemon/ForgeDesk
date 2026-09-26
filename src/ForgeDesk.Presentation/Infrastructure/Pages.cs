using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Infrastructure;

/// <summary>Declares which view model implements a top-level page. Registered by each feature.</summary>
public sealed record PageRegistration(PageKind Kind, Type ViewModelType);

/// <summary>
/// Creates top-level page view models (dashboard, activity, settings, onboarding). A page that no
/// feature registered is unavailable: navigation to it is ignored and its entry points are hidden.
/// </summary>
public interface IPageFactory
{
    bool IsAvailable(PageKind kind);

    /// <summary>Creates the page's view model, or returns null when the page is not registered.</summary>
    object? Create(PageKind kind);
}

/// <summary>Optional navigation hooks for pages and the project workspace.</summary>
public interface INavigationAware
{
    /// <summary>
    /// Called every time the page becomes the current page. <paramref name="argument"/> carries
    /// the navigation detail (a settings section name, a <c>WorkspaceNavigationRequest</c>…).
    /// </summary>
    Task OnNavigatedToAsync(object? argument);

    /// <summary>Called when another page replaces this one.</summary>
    void OnNavigatedFrom();
}

public static class PageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TViewModel"/> as the implementation of a top-level page.
    /// Register the view model itself too when it must be shared (otherwise it is created once,
    /// on first navigation, with its dependencies resolved from the container).
    /// </summary>
    public static IServiceCollection AddPage<TViewModel>(this IServiceCollection services, PageKind kind)
        where TViewModel : class
    {
        if (kind == PageKind.Project)
        {
            throw new ArgumentException("Project workspaces are created by the navigation service, not registered as pages.", nameof(kind));
        }

        services.AddSingleton(new PageRegistration(kind, typeof(TViewModel)));
        return services;
    }
}
