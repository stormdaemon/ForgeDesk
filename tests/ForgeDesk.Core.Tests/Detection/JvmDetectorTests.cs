using ForgeDesk.Core.Detection;

namespace ForgeDesk.Core.Tests.Detection;

public class JvmDetectorTests
{
    [Fact]
    public async Task Maven_spring_boot_project_prefers_the_wrapper()
    {
        using var fixture = new DetectionFixture()
            .With("pom.xml", """
                <project>
                  <parent><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-parent</artifactId></parent>
                  <dependencies>
                    <dependency><groupId>org.junit.jupiter</groupId><artifactId>junit-jupiter</artifactId></dependency>
                  </dependencies>
                </project>
                """)
            .With("mvnw", "#!/bin/sh")
            .With("mvnw.cmd", "@echo off")
            .With("src/main/java/com/acme/App.java", "class App {}")
            .With("src/test/java/com/acme/AppTest.java", "class AppTest {}");

        var profile = await fixture.DetectAsync();

        var maven = OperatingSystem.IsWindows() ? "mvnw.cmd" : "./mvnw";
        profile.Command("maven:package").Should().Match<DetectedCommand>(c => c.CommandLine == $"{maven} package" && c.Category == CommandCategory.Build);
        profile.Command("maven:test").CommandLine.Should().Be($"{maven} test");
        profile.Command("maven:verify").Category.Should().Be(CommandCategory.Test);
        profile.Command("maven:clean").Category.Should().Be(CommandCategory.Clean);
        profile.Command("maven:spring-boot-run").Should().Match<DetectedCommand>(c => c.CommandLine == $"{maven} spring-boot:run" && c.Category == CommandCategory.Run);
        profile.Technology("Spring Boot");
        profile.Technology("Java").Kind.Should().Be(TechnologyKind.Language);
        profile.Tests.Frameworks.Should().Contain("JUnit");
        profile.Tests.Locations.Should().Contain("src/test");
        profile.BuildSystems.Should().Contain("Maven");
    }

    [Fact]
    public async Task Maven_without_wrapper_uses_mvn()
    {
        using var fixture = new DetectionFixture().With("pom.xml", "<project><build><plugins><plugin><artifactId>kotlin-maven-plugin</artifactId></plugin></plugins></build></project>");

        var profile = await fixture.DetectAsync();

        profile.Command("maven:package").CommandLine.Should().Be("mvn package");
        profile.HasCommand("maven:spring-boot-run").Should().BeFalse();
        profile.Technology("Kotlin");
    }

    [Fact]
    public async Task Gradle_kotlin_android_project()
    {
        using var fixture = new DetectionFixture()
            .With("settings.gradle.kts", "rootProject.name = \"Notes\"\ninclude(\":app\")\n")
            .With("build.gradle.kts", "plugins {\n  id(\"com.android.application\") version \"8.7.0\" apply false\n  kotlin(\"android\") version \"2.0.0\" apply false\n}\n")
            .With("app/build.gradle.kts", "dependencies { testImplementation(\"junit:junit:4.13.2\") }\n")
            .With("gradlew", "#!/bin/sh")
            .With("gradlew.bat", "@echo off")
            .With("app/src/main/java/Notes.kt", "class Notes");

        var profile = await fixture.DetectAsync();

        var gradle = OperatingSystem.IsWindows() ? "gradlew.bat" : "./gradlew";
        profile.Command("gradle:build").Should().Match<DetectedCommand>(c => c.CommandLine == $"{gradle} build" && c.Category == CommandCategory.Build);
        profile.Command("gradle:test").Category.Should().Be(CommandCategory.Test);
        profile.Command("gradle:clean").Category.Should().Be(CommandCategory.Clean);
        profile.Command("gradle:assembleDebug").CommandLine.Should().Be($"{gradle} assembleDebug");
        profile.Technology("Android").Kind.Should().Be(TechnologyKind.Framework);
        profile.Technology("Kotlin");
        profile.Technology("Gradle").Kind.Should().Be(TechnologyKind.BuildTool);
        profile.Tests.Frameworks.Should().Contain("JUnit");
    }

    [Fact]
    public async Task Gradle_spring_boot_gets_bootRun_and_application_gets_run()
    {
        using var springBoot = new DetectionFixture().With("build.gradle", "plugins { id 'org.springframework.boot' version '3.4.0' }\n");
        using var application = new DetectionFixture().With("build.gradle", "plugins { id 'application' }\napplication { mainClass = 'acme.Main' }\n");

        var bootProfile = await springBoot.DetectAsync();
        var appProfile = await application.DetectAsync();

        bootProfile.Command("gradle:bootRun").Should().Match<DetectedCommand>(c => c.CommandLine == "gradle bootRun" && c.Category == CommandCategory.Run);
        bootProfile.Technology("Spring Boot");
        appProfile.Command("gradle:run").CommandLine.Should().Be("gradle run");
        appProfile.HasCommand("gradle:bootRun").Should().BeFalse();
    }
}
