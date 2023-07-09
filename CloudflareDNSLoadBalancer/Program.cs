using CloudflareDNSLoadBalancer;
using Serilog;
using Serilog.Core;

// var builder = Host.CreateApplicationBuilder(args);
var builder = Host.CreateApplicationBuilder(args);


builder.Services.AddSerilog(new LoggerConfiguration()
	.WriteTo.Console()
	.CreateLogger());
builder.Services.AddHostedService<Worker>();



var app = builder.Build();
app.Run();
