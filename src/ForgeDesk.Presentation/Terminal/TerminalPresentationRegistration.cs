using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Terminal;

public static class TerminalPresentationRegistration
{
    /// <summary>Registers the Terminal view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddTerminalPresentation(this IServiceCollection services)
    {
        services.AddWorkspaceSection<TerminalSectionViewModel>(WorkspaceSection.Terminal);
        return services;
    }
}
