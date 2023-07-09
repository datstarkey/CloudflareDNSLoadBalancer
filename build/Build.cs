using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.TeamCity;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.Helm;
using Serilog;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;


[TeamCity(VcsTriggeredTargets = new []{nameof(Push), nameof(UploadHelm)})]
class Build : NukeBuild
{
	public static int Main() => Execute<Build>(x => x.Push, x => x.UploadHelm);

	[Parameter] readonly string DockerUsername;
	[Parameter] readonly string DockerPassword;

	[Solution(GenerateProjects = true)] readonly Solution Solution;

	[GitVersion] readonly GitVersion GitVersion;

	readonly string DockerImageName = "datstarkey/cloudflare-dns-loadbalancer";

	AbsolutePath DockerFile => Solution.CloudflareDNSLoadBalancer.Directory / "Dockerfile";
	string DockerTag => $"{DockerImageName}:{GitVersion.SemVer}";

	readonly string HelmChartName = "CloudflareDnsLoadBalancer";
	string HelmChartPackage => RootDirectory / "Helm" / $"{HelmChartName}-{GitVersion.SemVer}.tgz";
	string HelmChartPath => RootDirectory / "Helm" / HelmChartName;

	Target Login => _ => _
		.Executes(() =>
			DockerTasks.DockerLogin(x => x
				.SetUsername(DockerUsername)
				.SetPassword(DockerPassword)
			)
		);

	Target Compile => _ => _
		.DependsOn(Login)
		.Executes(() =>
			DockerTasks.DockerBuild(x => x
				.SetPath(RootDirectory)
				.SetFile(DockerFile)
				.SetTag(DockerTag)
			)
		);

	Target Push => _ => _
		.DependsOn(Compile)
		.Executes(() => DockerTasks.DockerPush(x => x.SetName(DockerTag)));

	Target UploadHelm => _ => _
		.Executes(async () =>
		{
			HelmTasks.HelmPackage(x => x.SetVersion(GitVersion.SemVer).SetChartPaths(HelmChartPath).SetDestination(RootDirectory / "Helm"));
			using var client = new HttpClient();
			await client.DeleteAsync($"https://helm.starkeydigital.com/api/charts/{HelmChartName}/{GitVersion.SemVer}");
			var body = new ByteArrayContent(File.ReadAllBytes(HelmChartPackage));
			body.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
			var result = await client.PostAsync("https://helm.starkeydigital.com/api/charts", body);

			if (result.IsSuccessStatusCode)
			{
				Log.Information("Sent Helm Chart to Helm Repository");
			}
			else
			{
				Log.Error("{Message}", await result.Content.ReadAsStringAsync());
			}
			File.Delete(HelmChartPackage);
		});

}
