using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeDesk.Presentation.Onboarding;

public static class OnboardingPresentationRegistration
{
    /// <summary>Registers the first-launch onboarding page (the shell shows it until it is completed).</summary>
    public static IServiceCollection AddOnboardingPresentation(this IServiceCollection services)
    {
        services.AddPage<OnboardingViewModel>(PageKind.Onboarding);
        return services;
    }
}
