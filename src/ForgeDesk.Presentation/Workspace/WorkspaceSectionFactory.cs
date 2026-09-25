using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>Declares which view model implements a workspace section. Registered by each feature.</summary>
public sealed record WorkspaceSectionRegistration(WorkspaceSection Section, Type ViewModelType);

public sealed class WorkspaceSectionFactory : IWorkspaceSectionFactory
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<WorkspaceSection, Type> _types;

    public WorkspaceSectionFactory(IServiceProvider services, IEnumerable<WorkspaceSectionRegistration> registrations)
    {
        _services = services;
        _types = registrations.ToDictionary(r => r.Section, r => r.ViewModelType);
    }

    public IReadOnlyCollection<WorkspaceSection> AvailableSections => _types.Keys;

    public IWorkspaceSectionViewModel Create(WorkspaceSection section, ProjectContext context)
    {
        if (!_types.TryGetValue(section, out var type))
        {
            throw new InvalidOperationException($"No view model registered for workspace section {section}.");
        }

        return (IWorkspaceSectionViewModel)ActivatorUtilities.CreateInstance(_services, type, context);
    }
}

public static class WorkspaceSectionServiceCollectionExtensions
{
    /// <summary>Registers <typeparamref name="TViewModel"/> as the implementation of a workspace tab.</summary>
    public static IServiceCollection AddWorkspaceSection<TViewModel>(this IServiceCollection services, WorkspaceSection section)
        where TViewModel : class, IWorkspaceSectionViewModel
    {
        services.AddSingleton(new WorkspaceSectionRegistration(section, typeof(TViewModel)));
        return services;
    }
}
