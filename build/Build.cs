using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DocFX.DocFXTasks;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using Nuke.Common.ChangeLog;
using System.Collections.Generic;

[CheckBuildProjectConfigurations]
[DotNetVerbosityMapping]
[ShutdownDotNetAfterServerBuild]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Test);

    [CI] readonly GitHubActions GitHubActions;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [Required][GitVersion(Framework = "net6.0")] readonly GitVersion GitVersion;

    readonly string _githubContext = EnvironmentInfo.GetVariable<string>("GITHUB_CONTEXT");

    [Parameter] string NugetApiUrl = "https://api.nuget.org/v3/index.json";

    [Parameter] [Secret] string NuGetApiKey;

    [Parameter] [Secret] string GitHubApiKey;

    AbsolutePath OutputTests => RootDirectory / "TestResults";

    AbsolutePath OutputPerfTests => RootDirectory / "PerfResults";
    AbsolutePath DocSiteDirectory => RootDirectory / "docs/_site";
    public string ChangelogFile => RootDirectory / "CHANGELOG.md";
    public AbsolutePath DocFxDir => RootDirectory / "docs";
    public string DocFxDirJson => DocFxDir / "docfx.json";
    AbsolutePath OutputNuget => Output / "nuget";
    AbsolutePath Output => RootDirectory / "output";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath TestSourceDirectory => RootDirectory / "AvroSchemaGenerator.Tests";

    public ChangeLog Changelog => ReadChangelog(ChangelogFile);

    public ReleaseNotes LatestVersion => Changelog.ReleaseNotes.OrderByDescending(s => s.Version).FirstOrDefault() ?? throw new ArgumentException("Bad Changelog File. Version Should Exist");
    public string ReleaseVersion => LatestVersion.Version?.ToString() ?? throw new ArgumentException("Bad Changelog File. Define at least one version");

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            RootDirectory
            .GlobDirectories("**/bin", "**/obj", Output, OutputTests, OutputPerfTests, OutputNuget, DocSiteDirectory)
            .ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });
    IEnumerable<string> ChangelogSectionNotes => ExtractChangelogSectionNotes(ChangelogFile);

    Target RunChangelog => _ => _
        //.OnlyWhenStatic(() => InvokedTargets.Contains(nameof(RunChangelog)))
        .Executes(() =>
        {
            FinalizeChangelog(ChangelogFile, GitVersion.SemVer, GitRepository);

            Git($"add {ChangelogFile}");
            Git($"commit -S -m \"Finalize {Path.GetFileName(ChangelogFile)} for {GitVersion.SemVer}.\"");
            Git($"tag -f {GitVersion.SemVer}");
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            try
            {
                DotNetRestore(s => s
               .SetProjectFile(Solution));
            }
            catch (Exception ex)
            {
                Information(ex.ToString());
            }
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var version = LatestVersion;
            const string assemblyInfoContents = @"
            // <auto-generated/>
            using System.Reflection;
            [assembly: AssemblyMetadataAttribute(""githash"",""GIT_HASH"")]
            namespace System {
                internal static class AssemblyVersionInformation {
                    internal const System.String AssemblyMetadata_githash = ""GIT_HASH"";
                }
            }
            ";
            var final = assemblyInfoContents.Replace("GIT_HASH", GitRepository?.Commit ?? "");
            File.WriteAllText(RootDirectory / "AssemblyInfo.cs", final.TrimStart());
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetFileVersion(version.Version.ToString())
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });
    Target Test => _ => _
        .After(Compile)
        .Executes(() =>
        {
            var projectName = "AvroSchemaGenerator.Tests";
            var project = Solution.GetProjects("*.Tests").First();
            Information($"Running tests from {projectName}");
            DotNetTest(c => c
                   .SetProjectFile(project)
                   .SetConfiguration(Configuration.ToString())
                   .SetFramework("net6.0")   
                   .SetVerbosity(verbosity: DotNetVerbosity.Detailed)
                   .EnableNoBuild());
        });

    Target Pack => _ => _
      .DependsOn(Test)
      .Executes(() =>
      {
          var version = LatestVersion;
          var project = Solution.GetProject("AvroSchemaGenerator");
          DotNetPack(s => s
              .SetProject(project)
              .SetConfiguration(Configuration)
              .EnableNoBuild()
              
              .EnableNoRestore()
              .SetAssemblyVersion(version.Version.ToString())
              .SetVersion(version.Version.ToString())
              .SetPackageReleaseNotes(GetNuGetReleaseNotes(ChangelogFile, GitRepository))
              .SetDescription("Generate Avro Schema with support for RECURSIVE SCHEMA")
              .SetPackageTags("Avro", "Schema Generator")
              .AddAuthors("Ebere Abanonu (@mestical)")
              .SetPackageProjectUrl("https://github.com/eaba/AvroSchemaGenerator")
              .SetOutputDirectory(OutputNuget));

      });
    Target Release => _ => _
      .DependsOn(Pack)
      .Requires(() => NugetApiUrl)
      .Requires(() => !NuGetApiKey.IsNullOrEmpty())
      .Requires(() => !GitHubApiKey.IsNullOrEmpty())
      //.Requires(() => !BuildNumber.IsNullOrEmpty())
      .Requires(() => Configuration.Equals(Configuration.Release))
      .Executes(() =>
      {
          
          GlobFiles(OutputNuget, "*.nupkg")
              .Where(x => !x.EndsWith("symbols.nupkg"))
              .ForEach(x =>
              {
                  Assert.NotNullOrEmpty(x);
                  DotNetNuGetPush(s => s
                      .SetTargetPath(x)
                      .SetSource(NugetApiUrl)
                      .SetApiKey(NuGetApiKey)
                  );
              });
      });
    static void Information(string info)
    {
        Serilog.Log.Information(info);  
    }
}