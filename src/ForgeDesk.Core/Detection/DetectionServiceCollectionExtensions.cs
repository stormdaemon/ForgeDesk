using ForgeDesk.Core.Detection.Ecosystems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeDesk.Core.Detection;

public static class DetectionServiceCollectionExtensions
{
    /// <summary>Registers the Detection domain services.</summary>
    public static IServiceCollection AddDetectionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IProjectDetector, ProjectDetector>();
        services.TryAddSingleton<ICustomCommandStore, CustomCommandStore>();

        services.TryAddEnumerable(
        [
            ServiceDescriptor.Singleton<IEcosystemDetector, NodeDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, DenoDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, DotNetDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, RustDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, GoDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, PythonDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, MavenDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, GradleDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, PhpDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, RubyDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, DartDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, MakeDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, CMakeDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, JustDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, TaskfileDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, DockerDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, BuildScriptDetector>(),
            ServiceDescriptor.Singleton<IEcosystemDetector, VsCodeTasksDetector>(),
        ]);

        return services;
    }
}
