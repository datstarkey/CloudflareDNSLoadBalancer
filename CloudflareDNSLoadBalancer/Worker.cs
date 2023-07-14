namespace CloudflareDNSLoadBalancer;

using CloudFlare.Client;
using CloudFlare.Client.Api.Zones;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using k8s;
using k8s.Models;
using Microsoft.VisualBasic;
using System.Collections.Concurrent;

public class Worker : BackgroundService
{
	private readonly ILogger<Worker> _logger;
	private readonly string? _kubeConfigPath = Environment.GetEnvironmentVariable("KUBECONFIG");
	private readonly string? _cloudflareToken = Environment.GetEnvironmentVariable("CLOUDFLARE_TOKEN");
	private readonly bool? _cloudflareProxy = bool.Parse(Environment.GetEnvironmentVariable("CLOUDFLARE_PROXY")?.ToLower() ?? "false");
	private readonly Kubernetes _kubernetes;

	private static ConcurrentDictionary<string, List<string>> _storedValues = new ConcurrentDictionary<string, List<string>>();

	public Worker(ILogger<Worker> logger)
	{
		_logger = logger;

		if (_kubeConfigPath is null)
		{
			_logger.LogInformation("Using default configuration");
			_kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
		}
		else
		{
			_logger.LogInformation("Using configuration from {KUBECONFIG}", _kubeConfigPath);
			_kubernetes = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(_kubeConfigPath));
		}

	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{

		var services = _kubernetes.CoreV1.ListServiceForAllNamespacesWithHttpMessagesAsync(labelSelector: "cloudflaredns.kubernetes.io/hostname", cancellationToken: stoppingToken, watch: true);

		await foreach (var svc in services.WatchAsync<V1Service, V1ServiceList>(cancellationToken: stoppingToken))
		{
			_logger.LogInformation("----------------------");
			var uri = svc.Item2.Metadata.Labels["cloudflaredns.kubernetes.io/hostname"];
			var split = uri.Split(".");
			if (split.Length < 2)
			{
				_logger.LogError("Invalid hostname {Hostname}", uri);
				continue;
			}
			var host = split[^2] + "." + split.Last();
			var name = svc.Item2.Metadata.Name;

			_logger.LogInformation("Found service {Service} with Uri {Uri}", name, uri);

			if (svc.Item2.Status?.LoadBalancer?.Ingress is null)
			{
				_logger.LogWarning("Service {Service} does not have a load balancer", svc.Item2.Metadata.Name);
				continue;
			}

			var externalIps = svc.Item2.Status.LoadBalancer.Ingress.Select(x => x.Ip).ToList();

			//Skip if we already have the same ips in memory
			if (_storedValues.TryGetValue(name, out var storedIps))
			{
				if (externalIps.All(x => storedIps.Contains(x)))
					continue;
			}

			var ips = externalIps;
			_storedValues.AddOrUpdate(svc.Item2.Metadata.Name, externalIps, (k, v) => ips);

			//Determine if we only want to load balance to existing pods
			svc.Item2.Metadata.Labels.TryGetValue("cloudflaredns.kubernetes.io/exact-match", out var exactMatchString);
			if (bool.TryParse(exactMatchString, out var exactMatch) && exactMatch)
			{

				var nodeList = await _kubernetes.ListNodeAsync(cancellationToken: stoppingToken);
				//Get all available load balancing nodes
				var nodes = nodeList.Items.Where(x => x.Status.Addresses.Any(a => a.Type == "ExternalIP"))
					.ToDictionary(k => k.Name(), v => v.Status.Addresses.Where(x => x.Type == "ExternalIP")
						.Select(x => x.Address).ToList());

				_logger.LogDebug("Found Nodes: {Nodes}", nodes.Select(x => $"{x.Key}:{string.Join(",", x.Value)}"));

				var selector = svc.Item2.Spec.Selector;

				//Get all pods for the service
				var pods = await _kubernetes.ListNamespacedPodAsync(svc.Item2.Metadata.NamespaceProperty, labelSelector: string.Join(", ", selector.Select(x => $"{x.Key}={x.Value}")), cancellationToken: stoppingToken);

				if (pods.Items.Any())
				{

					_logger.LogDebug("Found Pods: {Pods}", pods.Items.Select(x => $"{x.Metadata.Name}:{x.Spec.NodeName}"));

					externalIps = pods.Items.Where(x => nodes.ContainsKey(x.Spec.NodeName))
						.SelectMany(x => nodes[x.Spec.NodeName])
						.ToList();
				}
				else
				{
					_logger.LogWarning("No pods found for service {Service}", svc.Item2.Metadata.Name);
				}
			}

			_logger.LogInformation("External Ips: {Ips}", externalIps);
			using var client = new CloudFlareClient(_cloudflareToken);
			var zones = await client.Zones.GetAsync(new ZoneFilter()
			{
				Name = host
			}, cancellationToken: stoppingToken);

			if (zones is null)
			{
				_logger.LogWarning("Could not find zone for {Host}", host);
				continue;
			}

			var zone = zones.Result.FirstOrDefault();
			if (zone is null)
			{
				_logger.LogWarning("Could not find zone for {Host}", host);
				continue;
			}

			_logger.LogInformation("Zone: {Name}", zone.Name);

			var records = await client.Zones.DnsRecords.GetAsync(zone.Id, new DnsRecordFilter()
			{
				Name = uri

			}, cancellationToken: stoppingToken);
			if (records.Result.Any())
			{
				_logger.LogInformation("Found Records {Join}", records.Result.Select(x => $"{x.Type:G}:{x.Name}"));

				foreach (var record in records.Result)
				{
					if (record.Type is not (DnsRecordType.A or DnsRecordType.Cname))
					{
						_logger.LogDebug("Ignoring {RecordName} as its not an A/Cname Record", record.Name);
					}
					else
					{
						if (externalIps.Contains(record.Content))
						{
							if (record.Proxied == _cloudflareProxy && record.Type == DnsRecordType.A)
							{
								_logger.LogInformation("Ignoring {RecordName} as its an A Record with the same IP and Proxy Status", record.Name);
								continue;
							}

							_logger.LogInformation("Updating {RecordName}", record.Name);
							await client.Zones.DnsRecords.UpdateAsync(zone.Id, record.Id, new ModifiedDnsRecord()
							{
								Content = record.Content,
								Name = record.Name,
								Proxied = _cloudflareProxy,
								Ttl = record.Ttl,
								Type = DnsRecordType.A
							}, cancellationToken: stoppingToken);
						}
						else
						{
							_logger.LogInformation("Deleting {RecordName} as its the Ip {Ip} is no longer needed", record.Name, record.Content);
							await client.Zones.DnsRecords.DeleteAsync(zone.Id, record.Id, cancellationToken: stoppingToken);
						}
					}
				}

				//Create any new records
				foreach (var ip in externalIps.Except(records.Result.Select(x => x.Content)))
				{
					await CreateRecord(client, zone.Id, uri, ip, stoppingToken);
				}
			}
			else
			{
				//Create all records
				foreach (var ip in externalIps)
				{
					await CreateRecord(client, zone.Id, uri, ip, stoppingToken);
				}
			}

		}
	}

	Task CreateRecord(CloudFlareClient client, string zoneId, string uri, string content, CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Creating new A Record for {Ip}", content);
		return client.Zones.DnsRecords.AddAsync(zoneId, new NewDnsRecord()
		{
			Content = content,
			Name = uri,
			Proxied = _cloudflareProxy,
			Ttl = 1,
			Type = DnsRecordType.A
		}, cancellationToken: cancellationToken);
	}

}
