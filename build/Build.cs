using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;

class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.Push);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter] readonly string DockerUsername;
    [Parameter] readonly string DockerPassword;
    
    [Solution(GenerateProjects = true)]
    readonly Solution Solution;
    
    [GitVersion]
    readonly GitVersion GitVersion;
    
    readonly string DockerImageName = "datstarkey/cloudflare-dns-loadbalancer";
    
    AbsolutePath DockerFile => Solution.CloudflareDNSLoadBalancer.Directory / "Dockerfile";
    string DockerTag => $"{DockerImageName}:{GitVersion.SemVer}";
    

    Target Login => _ => _
        .Executes(() =>
            DockerTasks.DockerLogin(x=>x
                .SetUsername(DockerUsername)
                .SetPassword(DockerPassword)    
            )
        );

    Target Compile => _ => _
        .DependsOn(Login)
        .Executes(() =>
            DockerTasks.DockerBuild(x=>x
                .SetPath(RootDirectory)
                .SetFile(DockerFile)
                .SetTag(DockerTag)
            )
        );

    Target Push => _ => _
        .DependsOn(Compile)
        .Executes(() => DockerTasks.DockerPush(x => x.SetName(DockerTag)));

}
