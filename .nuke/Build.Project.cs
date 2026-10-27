namespace Build;

/// <summary>
/// Defines project-specific settings and feature hooks for the shared MASS4 build.
/// </summary>
sealed partial class Build
{
    const string ProductName = "Graph";
    const string DefaultGithubRepository = "MASS4ORG/Graph";
    const string DefaultGitlabProjectPath = "mass4org/graph";
    const string ReleaseBotEmail = "massa+github@brunomassa.com";
    const string NuGetPackagePrefix = "MASS4.";
    const string PackageAuthors = "Bruno Massa";
    const string PackageCopyrightOwner = "Bruno Massa";
    const string PackageProjectUrl = "https://github.com/MASS4ORG/Graph";
    const string PackageRepositoryUrl = "https://github.com/MASS4ORG/Graph.git";
    const string PackageTags = "graph node-graph dataflow visual-scripting fsm headless";

    const bool DefaultPublishSelfContained = false;
    const bool DefaultPublishSingleFile = false;
    const bool DefaultPublishTrimmed = false;

    const string DefaultConfiguration = "Release";
    const string TestReportArgument = "--report-trx";

    IEnumerable<Project> ReleaseProjects => PackableProjects;
    IEnumerable<Project> VersionedProjects => PackableProjects;

    AbsolutePath PublishOutput(Project project) => PublishDir / project.Name;
    Dictionary<string, object> MsBuildProperties => [];
    Target[] RestoreDependencies => [];
    Target[] PackDependencies => [Publish];
    Target[] ReleaseCommitDependencies => [UpdateProjectVersions];

    [Parameter("Framework for published release archives (default: net10.0). NuGet packages include all target frameworks.")]
    readonly string PublishFramework = "net10.0";

    partial void ConfigurePublish(Project project, ref DotNetPublishSettings settings) =>
        settings = settings.SetFramework(PublishFramework);
}
