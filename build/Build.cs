using System;
using Nuke.Common;
using Nuke.Common.CI.TeamCity;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.Helm;
using Serilog;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;


[TeamCity(
	VcsTriggeredTargets = new []{nameof(UploadHelm), nameof(Push)},
	NonEntryTargets = new[]{nameof(Login), nameof(Compile)},
	Version = "2023.05")]
class Build : NukeBuild
{
	public static int Main() => Execute<Build>(x => x.Push, x => x.UploadHelm);

	[Parameter] readonly string DockerUsername;
	[Parameter] [Secret] readonly string DockerPassword;
	[Parameter] readonly string HelmUsername;
	[Parameter] [Secret] readonly string HelmPassword;

	[Solution(GenerateProjects = true)] readonly Solution Solution;

	[GitVersion] readonly GitVersion GitVersion;

	readonly string DockerImageName = "datstarkey/cloudflare-dns-loadbalancer";

	AbsolutePath DockerFile => Solution.CloudflareDNSLoadBalancer.Directory / "Dockerfile";
	string DockerTag => $"{DockerImageName}:{GitVersion.SemVer}";

	readonly string HelmChartName = "CloudflareDnsLoadBalancer";
	string HelmChartPackage => RootDirectory / "Helm" / $"{HelmChartName}-{GitVersion.SemVer}.tgz";
	string HelmChartPath => RootDirectory / "Helm" / HelmChartName;

	Target Login => _ => _
		.Requires(()=> DockerUsername)
		.Requires(()=> DockerPassword)
		.Unlisted()
		.Executes(() =>
			DockerTasks.DockerLogin(x => x
				.SetUsername(DockerUsername)
				.SetPassword(DockerPassword)
			)
		);

	Target Compile => _ => _
		.DependsOn(Login)
		.Unlisted()
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
		.Requires(()=> HelmUsername)
		.Requires(()=> HelmPassword)
		.Executes(async () =>
		{
			HelmTasks.HelmPackage(x => x.SetVersion(GitVersion.SemVer).SetChartPaths(HelmChartPath).SetDestination(RootDirectory / "Helm"));
			using var client = new HttpClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{HelmUsername}:{HelmPassword}")));
			
			//Delete any previous chart for overwriting
			await client.DeleteAsync($"https://helm.starkeydigital.com/api/charts/{HelmChartName}/{GitVersion.SemVer}");


			var result = await client.PostAsync("https://helm.starkeydigital.com/api/charts", new ByteArrayContent(File.ReadAllBytes(HelmChartPackage))
			{
				Headers =
				{
					ContentType = MediaTypeHeaderValue.Parse("application/octet-stream")
				}
			});

			if (result.IsSuccessStatusCode)
			{
				Log.Information("Sent Helm Chart to Helm Repository");
			}
			else
			{
				Log.Error("{Message}", await result.Content.ReadAsStringAsync());
			}
			
			//Delete the chart after uploading
			File.Delete(HelmChartPackage);
		});

}
