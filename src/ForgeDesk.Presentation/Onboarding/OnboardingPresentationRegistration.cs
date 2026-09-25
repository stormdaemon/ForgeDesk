using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Onboarding;

public static class OnboardingPresentationRegistration
{
    /// <summary>Registers the Onboarding view models (and their workspace sections / palette sources).</summary>
    public static IServiceCollection AddOnboardingPresentation(this IServiceCollection services)
    {
        return services;
    }
}
